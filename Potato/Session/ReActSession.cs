using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Prompts;
using Potato.Tools;

namespace Potato.Session;

internal sealed class ReActSession(
    AgentTools agentTools,
    ExecutionMemory executionMemory,
    PlanningService planningService)
{
    private const int MaxReActIterations = 12;
    private const int MaxToolCallsPerIteration = 4;
    private const int MaxConsecutiveInvalidReActResponses = 2;

    public async Task<string> ExecuteAsync(
        string goal,
        IReadOnlyList<AgentTask> plan,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        executionMemory.Clear();
        int successfulEditsBeforeExecution = agentTools.SuccessfulEditCount;
        int consecutiveInvalidResponses = 0;
        ChatOptions toolOptions = CreateToolOptions();
        string formattedPlan = FormatTaskList(plan);
        string projectMap = await planningService.BuildProjectMapAsync(
            Environment.CurrentDirectory,
            chatClient,
            cancellationToken);

        executionMemory.Add("ProjectMap", projectMap);

        var reactHistory = new List<ChatMessage>
        {
            new(ChatRole.System, PromptLibrary.ReActSystemPrompt),
            new(ChatRole.User, PromptLibrary.BuildReActInitialUserPrompt(
                goal,
                formattedPlan,
                Environment.CurrentDirectory,
                projectMap))
        };

        for (int iteration = 1; iteration <= MaxReActIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int memoryItemsBefore = executionMemory.Count;
            int toolCallsBefore = agentTools.ToolInvocationCount;

            PotatoConsole.WriteStatus($"ReAct iteration {iteration}/{MaxReActIterations}");
            ChatResponse response;
            using (PotatoConsole.StartProgress($"ReAct iteration {iteration}/{MaxReActIterations}..."))
            {
                agentTools.BeginToolInvocationBatch(MaxToolCallsPerIteration);
                try
                {
                    response = await chatClient.GetResponseAsync(reactHistory, toolOptions, cancellationToken);
                }
                finally
                {
                    agentTools.EndToolInvocationBatch();
                }
            }

            string responseText = string.IsNullOrWhiteSpace(response.Text)
                ? "No assistant response was returned."
                : response.Text.Trim();

            reactHistory.Add(new ChatMessage(ChatRole.Assistant, responseText));
            executionMemory.Add("Assistant ReAct response", responseText);

            if (IsFinalResponse(responseText))
            {
                if (RequiresSuccessfulEditBeforeFinal(goal, successfulEditsBeforeExecution))
                {
                    reactHistory.Add(new ChatMessage(
                        ChatRole.User,
                        "You returned FINAL for a project change, but no edit tool has successfully changed a file in this execution. Inspect the relevant file if needed, then use ApplySearchReplaceAsync, CreateFileAsync, or ApplyDiffPatchAsync. Do not claim completion until an edit tool reports success."));
                    continue;
                }

                return RemoveFinalMarker(responseText);
            }

            int toolCallsThisIteration = agentTools.ToolInvocationCount - toolCallsBefore;
            await executionMemory.SummarizeLargeUnsummarizedItemsAsync(chatClient, cancellationToken);

            if (toolCallsThisIteration > 0)
            {
                consecutiveInvalidResponses = 0;
                string observation = executionMemory.GetRange(memoryItemsBefore, executionMemory.Count, full: true);
                reactHistory.Add(new ChatMessage(
                    ChatRole.User,
                    PromptLibrary.BuildReActObservationUserPrompt(goal, formattedPlan, "native tool call", observation)));
                continue;
            }

            if (await TryExecuteTextualActionAsync(responseText, reactHistory, goal, formattedPlan, chatClient, cancellationToken))
            {
                consecutiveInvalidResponses = 0;
                continue;
            }

            consecutiveInvalidResponses++;
            if (TryReadUserIntervention(responseText, cancellationToken, out string userAnswer))
            {
                consecutiveInvalidResponses = 0;
                reactHistory.Add(new ChatMessage(ChatRole.User, $"User answered the model question:\n{userAnswer}"));
                continue;
            }

            if (LooksLikeUnavailableToolClaim(responseText))
            {
                reactHistory.Add(new ChatMessage(
                    ChatRole.User,
                    "The listed tools are available through this CLI. Continue with exactly one native tool call or one textual <tool_call> JSON block. Do not claim a tool is unavailable unless a tool observation proves it."));
                continue;
            }

            string retryInstruction = consecutiveInvalidResponses >= MaxConsecutiveInvalidReActResponses
                ? "The next response must be exactly one available tool call, or FINAL: only if the task is fully complete and verified."
                : "Continue with one concrete next action from the approved plan.";

            reactHistory.Add(new ChatMessage(
                ChatRole.User,
                $"""
                {retryInstruction}

                Original goal:
                {goal}

                Approved plan:
                {formattedPlan}

                Current working directory: {Environment.CurrentDirectory}
                """));
        }

        return $"Stopped after {MaxReActIterations} ReAct iterations without a FINAL response.";
    }

    private ChatOptions CreateToolOptions() =>
        new()
        {
            Tools =
            [
                AIFunctionFactory.Create(agentTools.GetCurrentTime),
                AIFunctionFactory.Create(agentTools.ReadFileContent),
                AIFunctionFactory.Create(agentTools.ListFiles),
                AIFunctionFactory.Create(agentTools.ListProjectFiles),
                AIFunctionFactory.Create(agentTools.SearchFiles),
                AIFunctionFactory.Create(agentTools.SearchFileContents),
                AIFunctionFactory.Create(agentTools.SummarizeFilePurpose),
                AIFunctionFactory.Create(agentTools.GetCollectedContext),
                AIFunctionFactory.Create(agentTools.ExecuteShellCommandAsync),
                AIFunctionFactory.Create(agentTools.ApplySearchReplaceAsync),
                AIFunctionFactory.Create(agentTools.CreateFileAsync),
                AIFunctionFactory.Create(agentTools.ApplyDiffPatchAsync)
            ],
            Temperature = 0.0f
        };

    private bool RequiresSuccessfulEditBeforeFinal(string goal, int successfulEditsBeforeExecution) =>
        ApprovalPolicy.IsProjectChangeRequest(goal) &&
        agentTools.SuccessfulEditCount <= successfulEditsBeforeExecution;

    private static string FormatTaskList(IReadOnlyList<AgentTask> tasks)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Planner produced this deterministic task list:");
        foreach (AgentTask task in tasks)
        {
            builder.AppendLine($"{task.Step}. {task.Action}: {task.Argument}");
            builder.AppendLine($"   Reason: {task.Reason}");
        }

        return builder.ToString().TrimEnd();
    }

    private static bool IsFinalResponse(string responseText) =>
        Regex.IsMatch(responseText, @"\A\s*(?:#{1,6}\s*)?(?:\*\*)?\s*FINAL\s*:?\s*(?:\*\*)?", RegexOptions.IgnoreCase);

    private static string RemoveFinalMarker(string responseText) =>
        Regex.Replace(
                responseText,
                @"\A\s*(?:#{1,6}\s*)?(?:\*\*)?\s*FINAL\s*:?\s*(?:\*\*)?",
                string.Empty,
                RegexOptions.IgnoreCase)
            .TrimStart();

    private async Task<bool> TryExecuteTextualActionAsync(
        string responseText,
        List<ChatMessage> reactHistory,
        string goal,
        string formattedPlan,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        TextualToolCall? toolCall = TryParseToolCall(responseText) ??
                                    TryParseSearchReplaceBlock(responseText) ??
                                    TryParseDiffPatchBlock(responseText) ??
                                    TryParseShellFence(responseText);
        if (toolCall is null)
        {
            return false;
        }

        PotatoConsole.WriteStatus($"Interpreting textual action as tool call: {toolCall.Name}");
        string result = await ExecuteTextualToolCallAsync(toolCall, cancellationToken);
        await executionMemory.SummarizeLargeUnsummarizedItemsAsync(chatClient, cancellationToken);
        reactHistory.Add(new ChatMessage(
            ChatRole.User,
            PromptLibrary.BuildReActObservationUserPrompt(goal, formattedPlan, toolCall.Name, result)));
        return true;
    }

    private async Task<string> ExecuteTextualToolCallAsync(TextualToolCall toolCall, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string toolName = NormalizeTextualToolName(toolCall.Name);
            agentTools.BeginToolInvocationBatch(1);
            try
            {
                return toolName switch
                {
                    nameof(AgentTools.GetCurrentTime) => agentTools.GetCurrentTime(),
                    nameof(AgentTools.ReadFileContent) => agentTools.ReadFileContent(
                        GetStringArgument(toolCall.Arguments, "filePath") ??
                        GetStringArgument(toolCall.Arguments, "file_path") ??
                        GetStringArgument(toolCall.Arguments, "path") ??
                        string.Empty),
                    nameof(AgentTools.ListFiles) => agentTools.ListFiles(
                        GetStringArgument(toolCall.Arguments, "directoryPath") ??
                        GetStringArgument(toolCall.Arguments, "directory_path"),
                        GetBoolArgument(toolCall.Arguments, "recursive") ?? false,
                        GetIntArgument(toolCall.Arguments, "maxEntries") ??
                        GetIntArgument(toolCall.Arguments, "max_entries") ??
                        200),
                    nameof(AgentTools.ListProjectFiles) => agentTools.ListProjectFiles(
                        GetStringArgument(toolCall.Arguments, "directoryPath") ??
                        GetStringArgument(toolCall.Arguments, "directory_path") ??
                        GetStringArgument(toolCall.Arguments, "path")),
                    nameof(AgentTools.SearchFiles) => agentTools.SearchFiles(
                        GetStringArgument(toolCall.Arguments, "searchTerms") ??
                        GetStringArgument(toolCall.Arguments, "search_terms") ??
                        GetStringArgument(toolCall.Arguments, "terms") ??
                        GetStringArgument(toolCall.Arguments, "query") ??
                        string.Empty,
                        GetStringArgument(toolCall.Arguments, "directoryPath") ??
                        GetStringArgument(toolCall.Arguments, "directory_path") ??
                        GetStringArgument(toolCall.Arguments, "path"),
                        GetBoolArgument(toolCall.Arguments, "recursive") ?? true,
                        GetBoolArgument(toolCall.Arguments, "matchCase") ??
                        GetBoolArgument(toolCall.Arguments, "match_case") ??
                        false,
                        GetIntArgument(toolCall.Arguments, "maxMatches") ??
                        GetIntArgument(toolCall.Arguments, "max_matches") ??
                        200),
                    nameof(AgentTools.SearchFileContents) => agentTools.SearchFileContents(
                        GetStringArgument(toolCall.Arguments, "searchTerms") ??
                        GetStringArgument(toolCall.Arguments, "search_terms") ??
                        GetStringArgument(toolCall.Arguments, "terms") ??
                        GetStringArgument(toolCall.Arguments, "query") ??
                        string.Empty,
                        GetStringArgument(toolCall.Arguments, "directoryPath") ??
                        GetStringArgument(toolCall.Arguments, "directory_path") ??
                        GetStringArgument(toolCall.Arguments, "path"),
                        GetStringArgument(toolCall.Arguments, "filePath") ??
                        GetStringArgument(toolCall.Arguments, "file_path"),
                        GetBoolArgument(toolCall.Arguments, "recursive") ?? true,
                        GetBoolArgument(toolCall.Arguments, "matchCase") ??
                        GetBoolArgument(toolCall.Arguments, "match_case") ??
                        false,
                        GetIntArgument(toolCall.Arguments, "maxMatches") ??
                        GetIntArgument(toolCall.Arguments, "max_matches") ??
                        100),
                    nameof(AgentTools.SummarizeFilePurpose) => await agentTools.SummarizeFilePurpose(
                        GetStringArgument(toolCall.Arguments, "filePath") ??
                        GetStringArgument(toolCall.Arguments, "file_path") ??
                        string.Empty),
                    nameof(AgentTools.GetCollectedContext) => agentTools.GetCollectedContext(
                        GetStringArgument(toolCall.Arguments, "index") ?? "list",
                        GetBoolArgument(toolCall.Arguments, "full") ?? false),
                    nameof(AgentTools.ApplyDiffPatchAsync) => await agentTools.ApplyDiffPatchAsync(
                        GetStringArgument(toolCall.Arguments, "patch") ?? string.Empty,
                        GetStringArgument(toolCall.Arguments, "workingDirectory") ??
                        GetStringArgument(toolCall.Arguments, "working_directory")),
                    nameof(AgentTools.ApplySearchReplaceAsync) => await agentTools.ApplySearchReplaceAsync(
                        GetStringArgument(toolCall.Arguments, "filePath") ??
                        GetStringArgument(toolCall.Arguments, "file_path") ??
                        GetStringArgument(toolCall.Arguments, "path") ??
                        string.Empty,
                        GetStringArgument(toolCall.Arguments, "search") ??
                        GetStringArgument(toolCall.Arguments, "oldString") ??
                        GetStringArgument(toolCall.Arguments, "old_string") ??
                        GetStringArgument(toolCall.Arguments, "SEARCH") ??
                        string.Empty,
                        GetStringArgument(toolCall.Arguments, "replace") ??
                        GetStringArgument(toolCall.Arguments, "newString") ??
                        GetStringArgument(toolCall.Arguments, "new_string") ??
                        GetStringArgument(toolCall.Arguments, "REPLACE") ??
                        string.Empty),
                    nameof(AgentTools.CreateFileAsync) => await agentTools.CreateFileAsync(
                        GetStringArgument(toolCall.Arguments, "filePath") ??
                        GetStringArgument(toolCall.Arguments, "file_path") ??
                        GetStringArgument(toolCall.Arguments, "path") ??
                        string.Empty,
                        GetStringArgument(toolCall.Arguments, "content") ??
                        GetStringArgument(toolCall.Arguments, "text") ??
                        string.Empty),
                    nameof(AgentTools.ExecuteShellCommandAsync) => await ExecuteShellToolCallAsync(toolCall),
                    _ => $"Error: Unknown textual tool call '{toolCall.Name}'."
                };
            }
            finally
            {
                agentTools.EndToolInvocationBatch();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error executing textual tool call '{toolCall.Name}': {ex.Message}";
        }
    }

    private async Task<string> ExecuteShellToolCallAsync(TextualToolCall toolCall)
    {
        string command = GetStringArgument(toolCall.Arguments, "command") ?? string.Empty;
        if (LooksLikeShellFileEditCommand(command))
        {
            return "Rejected shell-based file edit. Read the relevant file, then use ApplySearchReplaceAsync with exact SEARCH and REPLACE text.";
        }

        return await agentTools.ExecuteShellCommandAsync(
            command,
            GetStringArgument(toolCall.Arguments, "workingDirectory") ??
            GetStringArgument(toolCall.Arguments, "working_directory"),
            GetIntArgument(toolCall.Arguments, "timeoutSeconds") ??
            GetIntArgument(toolCall.Arguments, "timeout_seconds") ??
            60);
    }

    private static bool LooksLikeShellFileEditCommand(string command)
    {
        string normalized = command.ToLowerInvariant();
        return normalized.Contains(">>", StringComparison.Ordinal) ||
               Regex.IsMatch(normalized, @"(^|[^<])>([^>]|$)") ||
               normalized.Contains("sed -i", StringComparison.Ordinal) ||
               normalized.Contains("perl -pi", StringComparison.Ordinal) ||
               normalized.Contains("tee ", StringComparison.Ordinal);
    }

    private static TextualToolCall? TryParseToolCall(string responseText)
    {
        Match match = Regex.Match(
            responseText,
            @"<tool_call>\s*(?<json>\{[\s\S]*?\})\s*</tool_call>",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return null;
        }

        try
        {
            JsonNode? node = JsonNode.Parse(match.Groups["json"].Value);
            string? name = node?["name"]?.GetValue<string>();
            JsonObject? arguments = node?["arguments"] as JsonObject;
            return string.IsNullOrWhiteSpace(name)
                ? null
                : new TextualToolCall(name, arguments ?? []);
        }
        catch
        {
            return null;
        }
    }

    private static TextualToolCall? TryParseSearchReplaceBlock(string responseText)
    {
        Match aiderMatch = Regex.Match(
            responseText,
            @"(?<path>^[^\r\n<>`]+?)\s*\r?\n<<<<<<< SEARCH\r?\n(?<search>[\s\S]*?)\r?\n=======\r?\n(?<replace>[\s\S]*?)\r?\n>>>>>>> REPLACE",
            RegexOptions.Multiline);

        if (aiderMatch.Success)
        {
            return new TextualToolCall(
                nameof(AgentTools.ApplySearchReplaceAsync),
                new JsonObject
                {
                    ["filePath"] = aiderMatch.Groups["path"].Value.Trim(),
                    ["search"] = aiderMatch.Groups["search"].Value,
                    ["replace"] = aiderMatch.Groups["replace"].Value
                });
        }

        Match markdownMatch = Regex.Match(
            responseText,
            @"\*\*SEARCH\*\*\s*:?\s*```(?:[^\r\n`]*)?\r?\n(?<search>[\s\S]*?)\r?\n```\s*\*\*REPLACE\*\*\s*:?\s*```(?:[^\r\n`]*)?\r?\n(?<replace>[\s\S]*?)\r?\n```",
            RegexOptions.IgnoreCase);

        if (!markdownMatch.Success)
        {
            return null;
        }

        string? filePath = TryInferEditFilePath(responseText);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        return new TextualToolCall(
            nameof(AgentTools.ApplySearchReplaceAsync),
            new JsonObject
            {
                ["filePath"] = filePath,
                ["search"] = markdownMatch.Groups["search"].Value,
                ["replace"] = markdownMatch.Groups["replace"].Value
            });
    }

    private static TextualToolCall? TryParseDiffPatchBlock(string responseText)
    {
        Match match = Regex.Match(
            responseText,
            @"```diff\s*\r?\n(?<patch>[\s\S]*?)\r?\n```",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return null;
        }

        string patch = match.Groups["patch"].Value.Trim();
        if (!patch.Contains("--- ", StringComparison.Ordinal) ||
            !patch.Contains("+++ ", StringComparison.Ordinal) ||
            !patch.Contains("@@", StringComparison.Ordinal))
        {
            return null;
        }

        return new TextualToolCall(
            nameof(AgentTools.ApplyDiffPatchAsync),
            new JsonObject
            {
                ["patch"] = patch,
                ["workingDirectory"] = Environment.CurrentDirectory
            });
    }

    private static TextualToolCall? TryParseShellFence(string responseText)
    {
        Match match = Regex.Match(
            responseText,
            @"```(?:shell|bash|sh|powershell|pwsh|console|terminal)?\s*\r?\n(?<command>[\s\S]*?)```",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return null;
        }

        string command = match.Groups["command"].Value.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        return new TextualToolCall(
            nameof(AgentTools.ExecuteShellCommandAsync),
            new JsonObject
            {
                ["command"] = command,
                ["workingDirectory"] = Environment.CurrentDirectory,
                ["timeoutSeconds"] = 60
            });
    }

    private static string? TryInferEditFilePath(string responseText)
    {
        Match contextualMatch = Regex.Match(
            responseText,
            @"(?:file|in|to|target|edit|apply(?:ing)?(?:\s+this)?(?:\s+change)?(?:\s+to)?)\s*:?\s*`(?<path>[^`\r\n]+\.[A-Za-z0-9]+)`",
            RegexOptions.IgnoreCase);
        if (contextualMatch.Success)
        {
            return contextualMatch.Groups["path"].Value.Trim();
        }

        Match pathMatch = Regex.Match(
            responseText,
            @"(?<![`A-Za-z0-9_/\\.-])(?<path>[A-Za-z0-9_.-]+(?:[/\\][A-Za-z0-9_.-]+)*\.[A-Za-z0-9]+)(?![`A-Za-z0-9_/\\.-])");
        return pathMatch.Success ? pathMatch.Groups["path"].Value.Trim() : null;
    }

    private static bool LooksLikeUnavailableToolClaim(string responseText)
    {
        string normalized = responseText.ToLowerInvariant();
        return normalized.Contains("tool", StringComparison.Ordinal) &&
               (normalized.Contains("not available", StringComparison.Ordinal) ||
                normalized.Contains("unavailable", StringComparison.Ordinal) ||
                normalized.Contains("missing from the available", StringComparison.Ordinal));
    }

    private static bool TryReadUserIntervention(
        string modelQuestion,
        CancellationToken cancellationToken,
        out string userAnswer)
    {
        userAnswer = string.Empty;
        string normalized = modelQuestion.Trim().ToLowerInvariant();
        if (!normalized.Contains("?", StringComparison.Ordinal) ||
            !(normalized.EndsWith("?", StringComparison.Ordinal) ||
              normalized.Contains("do you want", StringComparison.Ordinal) ||
              normalized.Contains("please confirm", StringComparison.Ordinal) ||
              normalized.Contains("which ", StringComparison.Ordinal) ||
              normalized.Contains("what ", StringComparison.Ordinal)))
        {
            return false;
        }

        PotatoConsole.WriteStatus("Model requested user input during ReAct execution.");
        PotatoConsole.WriteAgentResponse(modelQuestion);
        userAnswer = PotatoConsole.ReadInterventionInput(cancellationToken);
        return true;
    }

    private static string NormalizeTextualToolName(string name)
    {
        string normalized = name.Trim();
        return normalized switch
        {
            "SearchReplace" or "search_replace" or "apply_search_replace" or "replace_file" => nameof(AgentTools.ApplySearchReplaceAsync),
            "CreateFile" or "create_file" or "write_new_file" or "new_file" => nameof(AgentTools.CreateFileAsync),
            "read_file" => nameof(AgentTools.ReadFileContent),
            "list_files" => nameof(AgentTools.ListFiles),
            "ListProjects" or "list_projects" or "list_project_files" or "project_inventory" => nameof(AgentTools.ListProjectFiles),
            "SearchFileContents" or "SearchInFiles" or "search_in_files" or "search_file_contents" or "grep" => nameof(AgentTools.SearchFileContents),
            "search_files" or "find_files" or "search_file_names" or "find_file_names" => nameof(AgentTools.SearchFiles),
            _ => normalized
        };
    }

    private static string? GetStringArgument(JsonObject arguments, string name)
    {
        JsonNode? node = arguments[name];
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch
        {
            return node.ToJsonString();
        }
    }

    private static int? GetIntArgument(JsonObject arguments, string name)
    {
        JsonNode? node = arguments[name];
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            return int.TryParse(node.ToString(), out int value) ? value : null;
        }
    }

    private static bool? GetBoolArgument(JsonObject arguments, string name)
    {
        JsonNode? node = arguments[name];
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch
        {
            return bool.TryParse(node.ToString(), out bool value) ? value : null;
        }
    }

    private sealed record TextualToolCall(string Name, JsonObject Arguments);
}
