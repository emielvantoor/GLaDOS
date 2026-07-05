using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

internal sealed partial class PotatoSession
{
    private const int MaxReActIterations = 12;
    private const int MaxToolCallsPerIteration = 8;

    private readonly Uri gladosEndpoint;
    private readonly GladosChatClientFactory clientFactory;
    private readonly ModelSelector modelSelector;
    private readonly ReActMemory reActMemory = new();
    private readonly AgentTools agentTools;
    private readonly FileMentionExpander fileMentionExpander = new();
    private readonly List<string> inputHistory = [];
    private readonly List<ChatMessage> chatHistory =
    [
        new(ChatRole.System, PromptLibrary.SystemPrompt)
    ];

    private AgentState currentState = AgentState.Specifying;
    private IChatClient currentOpenAiClient;
    private IChatClient currentClient;
    private string? latestSpecification;
    private string? latestApproach;
    private string? latestUserRequest;

    public PotatoSession(
        Uri gladosEndpoint,
        IChatClient openAiClient,
        IChatClient client,
        GladosChatClientFactory clientFactory,
        ModelSelector modelSelector)
    {
        this.gladosEndpoint = gladosEndpoint;
        this.clientFactory = clientFactory;
        this.modelSelector = modelSelector;
        currentOpenAiClient = openAiClient;
        currentClient = client;
        agentTools = new AgentTools(reActMemory);
    }

    public async Task RunAsync()
    {
        ChatOptions toolOptions = CreateToolOptions();
        await WriteUntrackedGreetingAsync();

        var slashCommandHandler = new SlashCommandHandler(
            gladosEndpoint,
            clientFactory,
            modelSelector,
            fileMentionExpander,
            ResetConversationState,
            () => PotatoConsole.WriteConversationTranscript(chatHistory),
            () => currentClient,
            SwitchModel);

        while (true)
        {
            string? userInput = PotatoConsole.ReadPromptInput(inputHistory);

            if (string.IsNullOrWhiteSpace(userInput))
            {
                continue;
            }

            AddInputHistory(userInput);

            if (userInput.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                userInput.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (userInput.Trim() == "?")
            {
                PotatoConsole.WriteShortcuts();
                continue;
            }

            if (await slashCommandHandler.TryHandleAsync(userInput))
            {
                toolOptions = CreateToolOptions();
                continue;
            }

            await HandleUserInputAsync(userInput, toolOptions);
        }
    }

    private void AddInputHistory(string input)
    {
        if (inputHistory.Count == 0 || !string.Equals(inputHistory[^1], input, StringComparison.Ordinal))
        {
            inputHistory.Add(input);
        }
    }

    private async Task HandleUserInputAsync(string userInput, ChatOptions toolOptions)
    {
        string messageForModel = fileMentionExpander.Expand(userInput);

        if (currentState == AgentState.Specifying)
        {
            if (latestSpecification is not null && ApprovalPolicy.IsUserApproval(userInput))
            {
                currentState = AgentState.Approaching;
                chatHistory.Add(new ChatMessage(ChatRole.User, PromptLibrary.ApprovalToApproachMessage(latestSpecification)));
            }
            else
            {
                latestUserRequest = messageForModel;
                chatHistory.Add(new ChatMessage(ChatRole.System, PromptLibrary.SpecificationGuardMessage));
                chatHistory.Add(new ChatMessage(ChatRole.User, messageForModel));
            }
        }
        else if (currentState == AgentState.Approaching)
        {
            if (latestApproach is not null && ApprovalPolicy.IsUserExecutionApproval(userInput))
            {
                currentState = AgentState.Confirmed;

                chatHistory.Add(new ChatMessage(ChatRole.System, PromptLibrary.BuildToolInstructions()));
                chatHistory.Add(new ChatMessage(
                    ChatRole.User,
                    PromptLibrary.ExecuteApprovedApproachMessage(
                        latestUserRequest,
                        latestSpecification,
                        latestApproach)));
            }
            else
            {
                chatHistory.Add(new ChatMessage(ChatRole.User, messageForModel));
            }
        }
        else
        {
            chatHistory.Add(new ChatMessage(ChatRole.User, messageForModel));
        }

        try
        {
            PotatoConsole.WriteStatus(currentState switch
            {
                AgentState.Specifying => "Generating specification...",
                AgentState.Approaching => "Generating approach...",
                _ => "Agent is executing ReAct loop..."
            });

            ChatOptions currentOptions = CreateCurrentOptions(toolOptions, includeTools: currentState == AgentState.Confirmed);

            if (currentState == AgentState.Confirmed)
            {
                await RunReActLoopAsync(currentOptions);
                ResetConversationState();
                return;
            }

            ChatResponse response = await currentClient.GetResponseAsync(chatHistory, currentOptions);
            chatHistory.Add(new ChatMessage(ChatRole.Assistant, response.Text));
            StoreLatestResponse(response.Text);
            PotatoConsole.WriteAgentResponse(response.Text);

            if (currentState == AgentState.Approaching &&
                ApprovalPolicy.ShouldAutoExecuteAfterApproach(latestUserRequest, latestSpecification, latestApproach))
            {
                currentState = AgentState.Confirmed;
                chatHistory.Add(new ChatMessage(ChatRole.System, PromptLibrary.BuildToolInstructions()));
                chatHistory.Add(new ChatMessage(
                    ChatRole.User,
                    PromptLibrary.ExecuteApprovedApproachMessage(
                        latestUserRequest,
                        latestSpecification,
                        latestApproach)));

                PotatoConsole.WriteStatus("Proceeding to ReAct execution...");
                await RunReActLoopAsync(CreateCurrentOptions(toolOptions, includeTools: true));
                ResetConversationState();
            }
            else if (currentState == AgentState.Approaching)
            {
                PotatoConsole.WriteStatus("Waiting for execution approval. Type 'execute' or 'yes' to start.");
            }
        }
        catch (Exception ex)
        {
            PotatoConsole.WriteError($"Error: {ex.Message}");
        }
    }

    private ChatOptions CreateToolOptions()
    {
        return new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create(agentTools.GetCurrentTime),
                AIFunctionFactory.Create(agentTools.ReadFileContent),
                AIFunctionFactory.Create(agentTools.ListFiles),
                AIFunctionFactory.Create(agentTools.SummarizeFilePurpose),
                AIFunctionFactory.Create(agentTools.GetCollectedContext),
                AIFunctionFactory.Create(agentTools.ApplyDiffPatchAsync),
                AIFunctionFactory.Create(agentTools.ExecuteShellCommandAsync)
            ]
        };
    }

    private static ChatOptions CreateCurrentOptions(ChatOptions toolOptions, bool includeTools)
    {
        return new ChatOptions
        {
            Tools = includeTools ? toolOptions.Tools : null
        };
    }

    private async Task RunReActLoopAsync(ChatOptions toolOptions)
    {
        for (int iteration = 1; iteration <= MaxReActIterations; iteration++)
        {
            int toolCallsBefore = agentTools.ToolInvocationCount;
            PotatoConsole.WriteStatus($"ReAct iteration {iteration}/{MaxReActIterations}...");
            PotatoConsole.WriteModelQuestion(GetLatestModelQuestion());

            ChatResponse response = await currentClient.GetResponseAsync(chatHistory, toolOptions);
            string responseText = response.Text.Trim();

            if (string.IsNullOrWhiteSpace(responseText))
            {
                responseText = "No assistant response was returned.";
            }

            chatHistory.Add(new ChatMessage(ChatRole.Assistant, responseText));
            reActMemory.Add("Assistant ReAct response", responseText);
            PotatoConsole.WriteModelExchange(iteration, GetLatestModelQuestion(), responseText);

            if (IsFinalResponse(responseText))
            {
                PotatoConsole.WriteAgentResponse(RemoveFinalMarker(responseText));
                return;
            }

            int toolCallsThisIteration = agentTools.ToolInvocationCount - toolCallsBefore;
            await reActMemory.SummarizeLargeUnsummarizedItemsAsync(currentOpenAiClient);
            if (toolCallsThisIteration <= 0)
            {
                if (await TryExecuteTextualActionAsync(responseText))
                {
                    continue;
                }

                if (await TryExecuteDeterministicFallbackAsync(iteration))
                {
                    continue;
                }

                PotatoConsole.WriteStatus("Model did not call a tool or return FINAL; continuing execution loop...");

                if (LooksLikeUnmarkedCompletion(responseText))
                {
                    chatHistory.Add(new ChatMessage(
                        ChatRole.User,
                        "You claimed the task is complete without using the required FINAL: marker and without a tool-backed observation. If the task is actually complete, respond exactly with FINAL: followed by the summary. Otherwise use one available tool for the next action."));
                    continue;
                }

                chatHistory.Add(new ChatMessage(
                    ChatRole.User,
                    PromptLibrary.RepeatCurrentStepMessage(
                        latestUserRequest ?? string.Empty,
                        Environment.CurrentDirectory,
                        GetLatestModelQuestion())));
                continue;
            }

            PotatoConsole.WriteAgentResponse(responseText);

            if (toolCallsThisIteration > MaxToolCallsPerIteration)
            {
                chatHistory.Add(new ChatMessage(
                    ChatRole.User,
                    $"You used {toolCallsThisIteration} tools in the previous iteration. Finish with FINAL: if the task is complete, otherwise continue with one targeted next action."));
                continue;
            }

            chatHistory.Add(new ChatMessage(
                ChatRole.User,
                PromptLibrary.NextStepAfterObservationMessage(
                    latestUserRequest ?? string.Empty,
                    Environment.CurrentDirectory,
                    "native tool call",
                    responseText)));
        }

        PotatoConsole.WriteError($"Stopped after {MaxReActIterations} ReAct iterations without a FINAL response.");
    }

    private static bool IsFinalResponse(string responseText) =>
        FinalMarkerRegex().IsMatch(responseText);

    private static bool LooksLikeUnmarkedCompletion(string responseText)
    {
        string normalized = responseText.ToLowerInvariant();
        return normalized.Contains("completed", StringComparison.Ordinal) ||
               normalized.Contains("has been implemented", StringComparison.Ordinal) ||
               normalized.Contains("has been completed", StringComparison.Ordinal) ||
               normalized.Contains("implementation has been tested", StringComparison.Ordinal) ||
               normalized.Contains("task is complete", StringComparison.Ordinal);
    }

    private static string RemoveFinalMarker(string responseText)
    {
        Match match = FinalMarkerRegex().Match(responseText);
        if (!match.Success)
        {
            return responseText.Trim();
        }

        if (match.Index == 0)
        {
            return FinalMarkerRegex().Replace(responseText, string.Empty, count: 1).TrimStart();
        }

        return responseText[..match.Index].TrimEnd();
    }

    [GeneratedRegex(@"^\s*(?:#{1,6}\s*)?(?:\*\*)?\s*FINAL\s*:?\s*(?:\*\*)?", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex FinalMarkerRegex();

    private async Task<bool> TryExecuteTextualActionAsync(string responseText)
    {
        TextualToolCall? toolCall = TryParseToolCall(responseText) ?? TryParseShellFence(responseText);
        if (toolCall is null)
        {
            return false;
        }

        PotatoConsole.WriteStatus($"Interpreting textual action as tool call: {toolCall.Name}");
        string result = await ExecuteTextualToolCallAsync(toolCall);
        await reActMemory.SummarizeLargeUnsummarizedItemsAsync(currentOpenAiClient);
        chatHistory.Add(new ChatMessage(
            ChatRole.User,
            PromptLibrary.NextStepAfterObservationMessage(
                latestUserRequest ?? string.Empty,
                Environment.CurrentDirectory,
                toolCall.Name,
                result)));
        return true;
    }

    private async Task<bool> TryExecuteDeterministicFallbackAsync(int iteration)
    {
        if (iteration != 1 ||
            !ShouldStartWithProjectInspection(latestUserRequest))
        {
            return false;
        }

        PotatoConsole.WriteStatus("Model did not choose the first project inspection action; running deterministic directory listing fallback...");
        string result = agentTools.ListFiles(Environment.CurrentDirectory);
        await reActMemory.SummarizeLargeUnsummarizedItemsAsync(currentOpenAiClient);

        chatHistory.Add(new ChatMessage(
            ChatRole.User,
            PromptLibrary.NextStepAfterObservationMessage(
                latestUserRequest ?? string.Empty,
                Environment.CurrentDirectory,
                nameof(AgentTools.ListFiles),
                result)));
        return true;
    }

    private static bool ShouldStartWithProjectInspection(string? request) =>
        ApprovalPolicy.IsProjectChangeRequest(request) ||
        ApprovalPolicy.IsReadOnlyInspectionRequest(request) && LooksLikeProjectOrFolderRequest(request);

    private static bool LooksLikeProjectOrFolderRequest(string? request)
    {
        string text = request?.ToLowerInvariant() ?? string.Empty;
        return text.Contains("project", StringComparison.Ordinal) ||
               text.Contains("folder", StringComparison.Ordinal) ||
               text.Contains("repo", StringComparison.Ordinal) ||
               text.Contains("repository", StringComparison.Ordinal);
    }

    private async Task<string> ExecuteTextualToolCallAsync(TextualToolCall toolCall)
    {
        try
        {
            return toolCall.Name switch
            {
                nameof(AgentTools.GetCurrentTime) => agentTools.GetCurrentTime(),
                nameof(AgentTools.ReadFileContent) => agentTools.ReadFileContent(
                    GetStringArgument(toolCall.Arguments, "filePath") ??
                    GetStringArgument(toolCall.Arguments, "file_path") ??
                    string.Empty),
                nameof(AgentTools.ListFiles) => agentTools.ListFiles(
                    GetStringArgument(toolCall.Arguments, "directoryPath") ??
                    GetStringArgument(toolCall.Arguments, "directory_path"),
                    GetBoolArgument(toolCall.Arguments, "recursive") ?? false,
                    GetIntArgument(toolCall.Arguments, "maxEntries") ??
                    GetIntArgument(toolCall.Arguments, "max_entries") ??
                    200),
                nameof(AgentTools.SummarizeFilePurpose) => agentTools.SummarizeFilePurpose(
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
                nameof(AgentTools.ExecuteShellCommandAsync) => await ExecuteShellToolCallAsync(toolCall),
                _ => $"Error: Unknown textual tool call '{toolCall.Name}'."
            };
        }
        catch (Exception ex)
        {
            return $"Error executing textual tool call '{toolCall.Name}': {ex.Message}";
        }
    }

    private async Task<string> ExecuteShellToolCallAsync(TextualToolCall toolCall)
    {
        string command = GetStringArgument(toolCall.Arguments, "command") ?? string.Empty;
        if (LooksLikeDirectoryListingCommand(command))
        {
            return "Rejected shell directory listing. Use the ListFiles tool instead.";
        }

        if (LooksLikeShellFileEditCommand(command))
        {
            return "Rejected shell-based file edit. Read the relevant file, then use ApplyDiffPatchAsync with a unified diff.";
        }

        return await agentTools.ExecuteShellCommandAsync(
            command,
            GetStringArgument(toolCall.Arguments, "workingDirectory") ??
            GetStringArgument(toolCall.Arguments, "working_directory"),
            GetIntArgument(toolCall.Arguments, "timeoutSeconds") ??
            GetIntArgument(toolCall.Arguments, "timeout_seconds") ??
            60);
    }

    private static bool LooksLikeDirectoryListingCommand(string command)
    {
        string normalized = command.TrimStart().ToLowerInvariant();
        return normalized.StartsWith("ls", StringComparison.Ordinal) ||
               normalized.StartsWith("dir", StringComparison.Ordinal) ||
               normalized.StartsWith("tree", StringComparison.Ordinal);
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
        var match = Regex.Match(
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

    private static TextualToolCall? TryParseShellFence(string responseText)
    {
        var match = Regex.Match(
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

    private string GetLatestModelQuestion()
    {
        for (int i = chatHistory.Count - 1; i >= 0; i--)
        {
            if (chatHistory[i].Role == ChatRole.User)
            {
                return chatHistory[i].Text;
            }
        }

        return "(no user message in chat history)";
    }

    private void StoreLatestResponse(string responseText)
    {
        if (currentState == AgentState.Specifying)
        {
            latestSpecification = responseText;
        }
        else if (currentState == AgentState.Approaching)
        {
            latestApproach = responseText;
        }
    }

    private async Task WriteUntrackedGreetingAsync()
    {
        try
        {
            var greetingMessages = new List<ChatMessage>
            {
                new(ChatRole.System, PromptLibrary.GreetingSystemPrompt),
                new(ChatRole.User, "Greet the user.")
            };

            ChatResponse greeting = await currentClient.GetResponseAsync(greetingMessages, new ChatOptions());
            PotatoConsole.WriteAgentResponse(greeting.Text);
        }
        catch (Exception ex)
        {
            PotatoConsole.WriteStatus($"Skipping startup greeting: {ex.Message}");
        }
    }

    private void SwitchModel(string selectedModel, IChatClient selectedOpenAiClient, IChatClient selectedClient)
    {
        currentOpenAiClient = selectedOpenAiClient;
        currentClient = selectedClient;
    }

    private void ResetConversationState()
    {
        currentState = AgentState.Specifying;
        reActMemory.Clear();
        latestSpecification = null;
        latestApproach = null;
        latestUserRequest = null;
        if (chatHistory.Count > 1)
        {
            chatHistory.RemoveRange(1, chatHistory.Count - 1);
        }
    }
}
