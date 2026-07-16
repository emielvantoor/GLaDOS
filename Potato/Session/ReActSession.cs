using System.Text;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Prompts;
using Potato.Tools;
using Potato.WebUi;

namespace Potato.Session;

internal sealed class ReActSession(
    AgentTools agentTools,
    ExecutionMemory executionMemory,
    PlanningService planningService,
    ContextCompactor contextCompactor)
{
    private const int MaxReActIterations = 24;
    private const int MaxToolCallsPerIteration = 1;
    private const int MaxConsecutiveInvalidReActResponses = 2;
    private const int ReActMaxOutputTokens = 8192;
    private const int MaxToolResultCharactersInHistory = 12_000;
    private const int MaxAssistantResponseCharactersInHistory = 4_000;
    private const int ContextUsageWarningThreshold = 70;  // Warn when context reaches 70% of limit
    private const int EstimatedTokensPerCharacter = 4;    // Rough estimate: 4 characters ≈ 1 token
    
    private readonly Queue<(int index, string source)> recentTruncations = new(5);

    public async Task<string> ExecuteAsync(
        string goal,
        string executionGuidance,
        IChatClient chatClient,
        Func<bool> getContextOptimizationEnabled,
        CancellationToken cancellationToken)
    {
        executionMemory.Clear();
        int successfulEditsBeforeExecution = agentTools.SuccessfulEditCount;
        int consecutiveInvalidResponses = 0;
        ChatOptions toolOptions = CreateToolOptions();
        string projectMap = await planningService.BuildProjectMapHeaderAsync(Environment.CurrentDirectory, cancellationToken);

        executionMemory.Add("ProjectMap", projectMap);

        var reactHistory = new List<ChatMessage>
        {
            new(ChatRole.System, PromptLibrary.BuildReActSystemPrompt(getContextOptimizationEnabled())),
            new(ChatRole.User, PromptLibrary.BuildReActInitialUserPrompt(
                goal,
                executionGuidance,
                Environment.CurrentDirectory,
                projectMap,
                getContextOptimizationEnabled()))
        };

        for (int iteration = 1; iteration <= MaxReActIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int memoryItemsBefore = executionMemory.Count;
            int toolCallsBefore = agentTools.ToolInvocationCount;

            ChatResponse response;
            using (PotatoConsole.StartProgress($"ReAct iteration {iteration}/{MaxReActIterations}..."))
            {
                agentTools.BeginToolInvocationBatch(MaxToolCallsPerIteration);
                try
                {
                    using IDisposable _ = PotatoModelCommunicationLogger.TrackMainPotatoChatContext();
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

            // Monitor context usage
            MonitorContextUsage(reactHistory);

            IReadOnlyList<FunctionCallContent> functionCalls = GetFunctionCalls(response);
            if (functionCalls.Count > 0)
            {
                FunctionCallContent functionCall = functionCalls[0];
                reactHistory.Add(new ChatMessage(ChatRole.Assistant, [functionCall]));
                string functionCallSummary = $"Function call: {functionCall.Name} ({functionCall.CallId})";
                executionMemory.Add("Assistant ReAct response", functionCallSummary);
                if (functionCalls.Count > 1)
                {
                    executionMemory.Add(
                        "Ignored extra function calls",
                        $"Ignored {functionCalls.Count - 1} extra tool call(s) because ReAct permits one tool call per iteration.");
                }

                string result = await ExecuteFunctionCallAsync(functionCall, cancellationToken);
                
                // Special handling for GetCollectedContext (retrieval tool, not data-gathering)
                // Retrieval tools should NOT be re-compacted, re-stored, or re-summarized
                if (functionCall.Name == nameof(AgentTools.GetCollectedContext) && !result.StartsWith("Error"))
                {
                    // Add full retrieval result directly to chat history (no truncation)
                    reactHistory.Add(new ChatMessage(
                        ChatRole.Tool,
                        [new FunctionResultContent(functionCall.CallId, result)]));
                    
                    consecutiveInvalidResponses = 0;
                    continue;  // Skip normal truncation → storage → summarization flow
                }
                
                // Intelligently compact the result for chat history (uses placeholder {{INDEX}})
                bool optimize = getContextOptimizationEnabled();
                
                if (optimize)
                {
                    // Context optimization enabled: truncate, store, summarize
                    ContextCompactor.CompactionResult compaction = contextCompactor.Compact(
                        result, 
                        DetectToolResultType(functionCall.Name),
                        MaxToolResultCharactersInHistory);
                    
                    // Store full result in execution memory - this assigns the actual index
                    int memoryIndex = executionMemory.Add(
                        $"{functionCall.Name}",
                        result,
                        DetectToolResultType(functionCall.Name),
                        compaction.OriginalLength,
                        compaction.RetrievalHint,
                        contextKey: null);
                    
                    // Replace placeholder index with actual memory index
                    string finalContent = compaction.TruncatedContent;
                    if (compaction.WasTruncated)
                    {
                        finalContent = finalContent.Replace("{{INDEX}}", memoryIndex.ToString());
                        
                        // Track recent truncation to enforce GetCollectedContext before edits
                        recentTruncations.Enqueue((memoryIndex, functionCall.Name));
                        if (recentTruncations.Count > 5)
                        {
                            recentTruncations.Dequeue();
                        }
                    }
                    
                    // Add compacted version to chat history (minified code goes directly, no summarization)
                    reactHistory.Add(new ChatMessage(
                        ChatRole.Tool,
                        [new FunctionResultContent(functionCall.CallId, finalContent)]));
                    
                    // await executionMemory.SummarizeLargeUnsummarizedItemsAsync(goal, chatClient, cancellationToken);
                }
                else
                {
                    // Context optimization disabled: passthrough mode - add raw result directly
                    reactHistory.Add(new ChatMessage(
                        ChatRole.Tool,
                        [new FunctionResultContent(functionCall.CallId, result)]));
                }
                
                consecutiveInvalidResponses = 0;
                continue;
            }

            AddResponseMessages(reactHistory, response, responseText, MaxAssistantResponseCharactersInHistory);
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
            // await executionMemory.SummarizeLargeUnsummarizedItemsAsync(goal, chatClient, cancellationToken);

            if (toolCallsThisIteration > 0)
            {
                consecutiveInvalidResponses = 0;
                string observation = CompactForReActHistory(
                    executionMemory.GetRange(memoryItemsBefore, executionMemory.Count, full: false),
                    MaxToolResultCharactersInHistory);
                reactHistory.Add(new ChatMessage(
                    ChatRole.User,
                    PromptLibrary.BuildReActObservationUserPrompt(goal, executionGuidance, "native tool call", observation, getContextOptimizationEnabled())));
                continue;
            }

            if (await TryExecuteTextualActionAsync(responseText, reactHistory, goal, executionGuidance, chatClient, getContextOptimizationEnabled, cancellationToken))
            {
                consecutiveInvalidResponses = 0;
                continue;
            }

            if (LooksLikeTruncatedToolResponse(responseText))
            {
                string message = BuildTruncatedToolResponseMessage(responseText);
                PotatoConsole.WriteError("Stopped: the model response appears to contain a truncated tool call. The full partial response is shown in the agent message.");
                return message;
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
                : "Continue with one concrete next action from the execution guidance.";

            reactHistory.Add(new ChatMessage(
                ChatRole.User,
                $"""
                {retryInstruction}

                Original goal:
                {goal}

                Execution guidance:
                {executionGuidance}

                Current working directory: {Environment.CurrentDirectory}
                """));
        }

        return $"Stopped after {MaxReActIterations} ReAct iterations without a FINAL response.";
    }

    private static void AddResponseMessages(
        List<ChatMessage> reactHistory,
        ChatResponse response,
        string fallbackText,
        int maxCharacters)
    {
        if (response.Messages.Count == 0)
        {
            reactHistory.Add(new ChatMessage(ChatRole.Assistant, CompactForReActHistory(fallbackText, maxCharacters)));
            return;
        }

        string responseText = FormatAssistantResponseForHistory(response);
        reactHistory.Add(new ChatMessage(
            ChatRole.Assistant,
            CompactForReActHistory(string.IsNullOrWhiteSpace(responseText) ? fallbackText : responseText, maxCharacters)));
    }

    private static string FormatAssistantResponseForHistory(ChatResponse response)
    {
        var builder = new StringBuilder();
        foreach (ChatMessage message in response.Messages)
        {
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                builder.AppendLine(message.Text);
                continue;
            }

            foreach (AIContent content in message.Contents)
            {
                if (content is TextContent text && !string.IsNullOrWhiteSpace(text.Text))
                {
                    builder.AppendLine(text.Text);
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<FunctionCallContent> GetFunctionCalls(ChatResponse response)
    {
        var functionCalls = new List<FunctionCallContent>();
        foreach (ChatMessage message in response.Messages)
        {
            foreach (AIContent content in message.Contents)
            {
                if (content is FunctionCallContent candidate)
                {
                    functionCalls.Add(candidate);
                }
            }
        }

        return functionCalls;
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
                AIFunctionFactory.Create(SearchProjectMapAsync),
                AIFunctionFactory.Create(agentTools.SearchFiles),
                AIFunctionFactory.Create(agentTools.SearchFileContents),
                AIFunctionFactory.Create(agentTools.SummarizeFilePurpose),
                AIFunctionFactory.Create(agentTools.GetCollectedContext),
                AIFunctionFactory.Create(agentTools.ExecuteShellCommandAsync),
                AIFunctionFactory.Create(agentTools.ApplySearchReplaceAsync),
                AIFunctionFactory.Create(agentTools.CreateFileAsync),
                AIFunctionFactory.Create(agentTools.ApplyDiffPatchAsync)
            ],
            Temperature = 0.0f,
            MaxOutputTokens = ReActMaxOutputTokens
        };

    [Description("Searches Potato's cached ProjectMap for likely relevant source, test, documentation, or project files. Use this for targeted repository discovery before exact file reads when the file path is not already known.")]
    public async Task<string> SearchProjectMapAsync(
        [Description("Focused search terms such as file name, folder, class, feature, symbol, or concept.")] string query,
        [Description("Maximum number of matching ProjectMap entries to return. Defaults to 12 and is capped by the runtime.")] int maxResults = 12)
    {
        if (!agentTools.TryReserveExternalToolInvocation(nameof(SearchProjectMapAsync), out string rejectionReason))
        {
            return agentTools.RejectExternalToolInvocation(nameof(SearchProjectMapAsync), rejectionReason);
        }

        PotatoConsole.WriteStatus($"Tool call: {nameof(SearchProjectMapAsync)} query={query}");
        string result = await planningService.SearchProjectMapAsync(
            Environment.CurrentDirectory,
            query,
            maxResults,
            chatClient: null,
            agentTools.CurrentCancellationToken);
        executionMemory.Add(nameof(SearchProjectMapAsync), result);
        return result;
    }

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
        string executionGuidance,
        IChatClient chatClient,
        Func<bool> getContextOptimizationEnabled,
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
        
        // Intelligently compact the result
        bool optimize = getContextOptimizationEnabled();
        
        if (optimize)
        {
            ContextCompactor.CompactionResult compaction = contextCompactor.Compact(
                result,
                DetectToolResultType(toolCall.Name),
                MaxToolResultCharactersInHistory);
            
            // Store full result in execution memory - gets assigned index
            int memoryIndex = executionMemory.Add(
                $"{toolCall.Name}",
                result,
                DetectToolResultType(toolCall.Name),
                compaction.OriginalLength,
                compaction.RetrievalHint,
                contextKey: null);
            
            // Replace placeholder with actual index
            string finalContent = compaction.TruncatedContent;
            if (compaction.WasTruncated)
            {
                finalContent = finalContent.Replace("{{INDEX}}", memoryIndex.ToString());
                
                // Track recent truncation to enforce GetCollectedContext before edits
                recentTruncations.Enqueue((memoryIndex, toolCall.Name));
                if (recentTruncations.Count > 5)
                {
                    recentTruncations.Dequeue();
                }
            }
            
            // await executionMemory.SummarizeLargeUnsummarizedItemsAsync(goal, chatClient, cancellationToken);
            
            // Add to chat history with correct index (minified content goes directly, no summarization)
            reactHistory.Add(new ChatMessage(
                ChatRole.User,
                PromptLibrary.BuildReActObservationUserPrompt(
                    goal,
                    executionGuidance,
                    toolCall.Name,
                    finalContent,
                    getContextOptimizationEnabled())));
        }
        else
        {
            // Context optimization disabled: passthrough mode - add raw result directly
            reactHistory.Add(new ChatMessage(
                ChatRole.User,
                PromptLibrary.BuildReActObservationUserPrompt(
                    goal,
                    executionGuidance,
                    toolCall.Name,
                    result,
                    getContextOptimizationEnabled())));
        }
        return true;
    }

    private static ToolResultType DetectToolResultType(string toolName) =>
        toolName switch
        {
            nameof(AgentTools.ReadFileContent) => ToolResultType.FileContent,
            nameof(AgentTools.ListFiles) or nameof(AgentTools.ListProjectFiles) => ToolResultType.DirectoryListing,
            nameof(AgentTools.SearchFiles) or nameof(AgentTools.SearchFileContents) or "SearchProjectMapAsync" => ToolResultType.SearchResults,
            nameof(AgentTools.ExecuteShellCommandAsync) => ToolResultType.ShellOutput,
            nameof(AgentTools.ApplyDiffPatchAsync) => ToolResultType.PatchDiff,
            nameof(AgentTools.SummarizeFilePurpose) => ToolResultType.Summary,
            nameof(AgentTools.GetCurrentTime) => ToolResultType.SystemInfo,
            _ => ToolResultType.Generic
        };

    private static bool IsEditTool(string toolName) =>
        toolName switch
        {
            nameof(AgentTools.CreateFileAsync) => true,
            nameof(AgentTools.ApplySearchReplaceAsync) => true,
            nameof(AgentTools.ApplyDiffPatchAsync) => true,
            _ => false
        };

    private static string CompactForReActHistory(string value, int maxCharacters)
    {
        string trimmed = value.Trim();
        if (trimmed.Length <= maxCharacters)
        {
            return trimmed;
        }

        return trimmed[..maxCharacters] +
               $"\n...(truncated for ReAct history after {maxCharacters:N0} characters; use GetCollectedContext if exact earlier content is needed)";
    }

    private async Task<string> ExecuteFunctionCallAsync(
        FunctionCallContent functionCall,
        CancellationToken cancellationToken)
    {
        var arguments = new JsonObject();
        IDictionary<string, object?>? sourceArguments = functionCall.Arguments;
        if (sourceArguments is not null)
        {
            foreach ((string key, object? value) in sourceArguments)
            {
                arguments[key] = ConvertArgumentToJsonNode(value);
            }
        }

        return await ExecuteTextualToolCallAsync(
            new TextualToolCall(functionCall.Name, arguments),
            cancellationToken);
    }

    private static JsonNode? ConvertArgumentToJsonNode(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonNode node)
        {
            return node.DeepClone();
        }

        if (value is JsonElement element)
        {
            return JsonNode.Parse(element.GetRawText());
        }

        return JsonSerializer.SerializeToNode(value);
    }

    private async Task<string> ExecuteTextualToolCallAsync(TextualToolCall toolCall, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string toolName = NormalizeTextualToolName(toolCall.Name);
            
            // Check if this is an edit tool and if there's recent truncated context
            if (IsEditTool(toolName) && recentTruncations.Count > 0)
            {
                // Get the most recent truncation info
                var (truncationIndex, truncationSource) = recentTruncations.Peek();
                
                // Smart blocking: Check if summary has HIGH confidence
                // If HIGH confidence, summary is sufficient - allow edit
                // If MEDIUM/LOW/Unknown, block and require GetCollectedContext
                var confidenceLevel = executionMemory.GetSummaryConfidence(truncationIndex);
                
                if (confidenceLevel == Potato.Models.ConfidenceLevel.High)
                {
                    // Summary is sufficient for the goal - allow edit ✓
                    // (Don't block, let execution continue)
                }
                else
                {
                    // Summary missing, low, or medium confidence - block edit
                    return $"⚠️ Cannot proceed with {toolName}: You just received truncated content (ref#{truncationIndex} from {truncationSource}).\n\n" +
                           $"The summary confidence is {confidenceLevel}. You MUST call GetCollectedContext(\"{truncationIndex}\", full=true) first to retrieve the complete file content.\n\n" +
                           $"Full content is required before performing edits. Retrieve the full context, then retry your edit operation.";
                }
            }
            
            agentTools.BeginToolInvocationBatch(1);
            try
            {
                string result = toolName switch
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
                    nameof(SearchProjectMapAsync) => await SearchProjectMapAsync(
                        GetStringArgument(toolCall.Arguments, "query") ??
                        GetStringArgument(toolCall.Arguments, "searchTerms") ??
                        GetStringArgument(toolCall.Arguments, "search_terms") ??
                        string.Empty,
                        GetIntArgument(toolCall.Arguments, "maxResults") ??
                        GetIntArgument(toolCall.Arguments, "max_results") ??
                        12),
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
                
                // If GetCollectedContext was successfully called, clear the truncation blocking
                if (toolName == nameof(AgentTools.GetCollectedContext) && !result.StartsWith("Error"))
                {
                    recentTruncations.Clear();
                }
                
                return result;
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
            @"<tool_?call>\s*(?<json>\{[\s\S]*?\})\s*</tool_?call>",
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

    private static bool LooksLikeTruncatedToolResponse(string responseText)
    {
        string trimmed = responseText.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        string lower = trimmed.ToLowerInvariant();
        if ((lower.Contains("<tool_call", StringComparison.Ordinal) ||
             lower.Contains("<toolcall", StringComparison.Ordinal)) &&
            !(lower.Contains("</tool_call>", StringComparison.Ordinal) ||
              lower.Contains("</toolcall>", StringComparison.Ordinal)))
        {
            return true;
        }

        if (ContainsEditToolName(lower) &&
            (HasUnbalancedJson(trimmed) ||
             HasUnclosedStringLiteral(trimmed) ||
             LooksLikeIncompleteToolJson(trimmed)))
        {
            return true;
        }

        if (lower.Contains("<<<<<<< search", StringComparison.Ordinal) &&
            !lower.Contains(">>>>>>> replace", StringComparison.Ordinal))
        {
            return true;
        }

        if (lower.Contains("**search**", StringComparison.Ordinal) &&
            !lower.Contains("**replace**", StringComparison.Ordinal))
        {
            return true;
        }

        if (lower.Contains("```diff", StringComparison.Ordinal) &&
            !trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            return true;
        }

        if ((lower.Contains("createfileasync", StringComparison.Ordinal) ||
             lower.Contains("create_file", StringComparison.Ordinal) ||
             lower.Contains("write_new_file", StringComparison.Ordinal)) &&
            lower.Contains("\"content\"", StringComparison.Ordinal) &&
            HasUnbalancedJson(trimmed))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsEditToolName(string lowerText) =>
        lowerText.Contains("applysearchreplaceasync", StringComparison.Ordinal) ||
        lowerText.Contains("apply_search_replace", StringComparison.Ordinal) ||
        lowerText.Contains("search_replace", StringComparison.Ordinal) ||
        lowerText.Contains("createfileasync", StringComparison.Ordinal) ||
        lowerText.Contains("create_file", StringComparison.Ordinal) ||
        lowerText.Contains("write_new_file", StringComparison.Ordinal) ||
        lowerText.Contains("applydiffpatchasync", StringComparison.Ordinal) ||
        lowerText.Contains("apply_diff_patch", StringComparison.Ordinal);

    private static bool LooksLikeIncompleteToolJson(string text) =>
        Regex.IsMatch(text, @"""(?:name|arguments|filePath|file_path|path|content|search|replace|patch)""\s*:\s*$", RegexOptions.IgnoreCase) ||
        Regex.IsMatch(text, @"""(?:content|search|replace|patch)""\s*:\s*""[\s\S]*\z", RegexOptions.IgnoreCase);

    private static bool HasUnbalancedJson(string text)
    {
        int objectDepth = 0;
        int arrayDepth = 0;
        bool inString = false;
        bool escaped = false;

        foreach (char current in text)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (current == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            switch (current)
            {
                case '{':
                    objectDepth++;
                    break;
                case '}':
                    objectDepth--;
                    break;
                case '[':
                    arrayDepth++;
                    break;
                case ']':
                    arrayDepth--;
                    break;
            }

            if (objectDepth < 0 || arrayDepth < 0)
            {
                return true;
            }
        }

        return inString || objectDepth > 0 || arrayDepth > 0;
    }

    private static bool HasUnclosedStringLiteral(string text)
    {
        bool inString = false;
        bool escaped = false;

        foreach (char current in text)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (current == '"')
            {
                inString = !inString;
            }
        }

        return inString;
    }

    private static string BuildTruncatedToolResponseMessage(string responseText)
    {
        return $"""
        Stopped: the model response appears to contain a truncated tool call.

        This usually means the ReAct prompt is too close to the context limit or the requested edit payload is too large for one model response. Potato did not execute the partial tool call because it may corrupt a file.

        Full partial model response:
        ```text
        {responseText}
        ```

        Try again with a narrower request, split the file creation/edit into smaller pieces, reduce prior context, or increase the model context window.
        """;
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
            "ApplySearchReplace" or "SearchReplace" or "search_replace" or "apply_search_replace" or "replace_file" => nameof(AgentTools.ApplySearchReplaceAsync),
            "CreateFile" or "create_file" or "write_new_file" or "new_file" => nameof(AgentTools.CreateFileAsync),
            "ApplyDiffPatch" or "apply_diff_patch" or "diff_patch" => nameof(AgentTools.ApplyDiffPatchAsync),
            "ExecuteShellCommand" or "execute_shell_command" or "shell" => nameof(AgentTools.ExecuteShellCommandAsync),
            "read_file" => nameof(AgentTools.ReadFileContent),
            "list_files" => nameof(AgentTools.ListFiles),
            "ListProjects" or "list_projects" or "list_project_files" or "project_inventory" => nameof(AgentTools.ListProjectFiles),
            "SearchProjectMap" or "search_project_map" or "project_map_search" or "search-project-map" => nameof(SearchProjectMapAsync),
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

    /// <summary>
    /// Monitor context usage and warn when approaching capacity.
    /// Estimates token count from character count.
    /// </summary>
    private void MonitorContextUsage(List<ChatMessage> reactHistory, int estimatedLmTokenLimit = 100_000)
    {
        // Estimate total context size from chat history
        int estimatedCharacters = reactHistory.Sum(msg =>
        {
            int charCount = 0;
            foreach (var content in msg.Contents ?? [])
            {
                charCount += content switch
                {
                    TextContent tc => tc.Text?.Length ?? 0,
                    FunctionResultContent frc => (frc.Result?.ToString() ?? "")?.Length ?? 0,
                    _ => 0
                };
            }
            return charCount;
        });

        int estimatedTokens = (estimatedCharacters / EstimatedTokensPerCharacter) + ReActMaxOutputTokens;
        int usagePercentage = (int)((estimatedTokens / (double)estimatedLmTokenLimit) * 100);

        if (usagePercentage >= ContextUsageWarningThreshold)
        {
            PotatoConsole.WriteStatus(
                $"⚠️  Context usage at {usagePercentage}% ({estimatedTokens:N0} / {estimatedLmTokenLimit:N0} tokens). " +
                $"Consider starting a new session or archiving old discussions.");
        }
    }
}

