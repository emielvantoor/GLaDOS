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
    private readonly PotatoRuntimeOptions options;
    private readonly PotatoAppSettingsStore appSettingsStore;
    private readonly List<string> inputHistory = [];
    private readonly List<ChatMessage> chatHistory =
    [
        new(ChatRole.System, PromptLibrary.SystemPrompt)
    ];

    private readonly object taskCancellationLock = new();
    private AgentState currentState = AgentState.Specifying;
    private IChatClient currentOpenAiClient;
    private IChatClient currentClient;
    private string? latestSpecification;
    private string? latestApproach;
    private string? latestUserRequest;
    private CancellationTokenSource? currentTaskCancellationSource;

    public PotatoSession(
        Uri gladosEndpoint,
        IChatClient openAiClient,
        IChatClient client,
        GladosChatClientFactory clientFactory,
        ModelSelector modelSelector,
        PotatoRuntimeOptions options,
        PotatoAppSettingsStore appSettingsStore)
    {
        this.gladosEndpoint = gladosEndpoint;
        this.clientFactory = clientFactory;
        this.modelSelector = modelSelector;
        this.options = options;
        this.appSettingsStore = appSettingsStore;
        currentOpenAiClient = openAiClient;
        currentClient = client;
        agentTools = new AgentTools(reActMemory, () => currentOpenAiClient, options);
    }

    public async Task RunAsync()
    {
        ChatOptions toolOptions = CreateToolOptions();
        await WriteUntrackedGreetingAsync();
        ConsoleCancelEventHandler cancelHandler = HandleConsoleCancelKeyPress;
        Console.CancelKeyPress += cancelHandler;

        var slashCommandHandler = new SlashCommandHandler(
            gladosEndpoint,
            clientFactory,
            modelSelector,
            fileMentionExpander,
            ResetConversationState,
            SetUseCompiledDefaultPrompts,
            appSettingsStore.SetSelectedModel,
            () => PotatoConsole.WriteConversationTranscript(chatHistory),
            () => currentClient,
            SwitchModel);

        try
        {
            while (true)
            {
                string? userInput = PotatoConsole.ReadPromptInput(inputHistory, GetPromptPlaceholder());

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
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private void AddInputHistory(string input)
    {
        if (inputHistory.Count == 0 || !string.Equals(inputHistory[^1], input, StringComparison.Ordinal))
        {
            inputHistory.Add(input);
        }
    }

    private string GetPromptPlaceholder()
    {
        if (currentState == AgentState.Specifying && latestSpecification is not null)
        {
            return "yes/ok to approve, or type requested changes";
        }

        if (currentState == AgentState.Approaching && latestApproach is not null)
        {
            return "execute/yes to start, or type requested changes";
        }

        return "Type your message or @path/to/file";
    }

    private async Task HandleUserInputAsync(string userInput, ChatOptions toolOptions)
    {
        using CancellationTokenSource taskCancellationSource = BeginTaskCancellation();
        CancellationToken cancellationToken = taskCancellationSource.Token;
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
                await RunReActLoopAsync(currentOptions, cancellationToken);
                ResetConversationState();
                return;
            }

            ChatResponse response;
            using (PotatoConsole.StartProgress("Waiting for response..."))
            {
                response = await currentClient.GetResponseAsync(chatHistory, currentOptions, cancellationToken);
            }

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
                await RunReActLoopAsync(CreateCurrentOptions(toolOptions, includeTools: true), cancellationToken);
                ResetConversationState();
            }
            else if (currentState == AgentState.Approaching)
            {
                PotatoConsole.WriteStatus("Waiting for execution approval. Type 'execute' or 'yes' to start.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ResetConversationState();
            PotatoConsole.WriteSuccess("Aborted current task. Back at the main prompt.");
        }
        catch (Exception ex)
        {
            PotatoConsole.WriteError($"Error: {ex.Message}");
        }
        finally
        {
            EndTaskCancellation(taskCancellationSource);
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
                AIFunctionFactory.Create(agentTools.ApplySearchReplaceAsync),
                AIFunctionFactory.Create(agentTools.CreateFileAsync),
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

    private async Task RunReActLoopAsync(ChatOptions toolOptions, CancellationToken cancellationToken)
    {
        int successfulEditsBeforeExecution = agentTools.SuccessfulEditCount;
        bool directCreateFileFallbackAttempted = false;
        bool directReplaceFileFallbackAttempted = false;

        for (int iteration = 1; iteration <= MaxReActIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int toolCallsBefore = agentTools.ToolInvocationCount;
            int memoryItemsBeforeToolCalls = reActMemory.Count;
            PotatoConsole.WriteStatus($"ReAct iteration {iteration}/{MaxReActIterations}...");
            if (options.Verbose)
            {
                PotatoConsole.WriteModelQuestion(GetLatestModelQuestion());
            }

            ChatResponse response;
            using (PotatoConsole.StartProgress($"ReAct iteration {iteration}/{MaxReActIterations}..."))
            {
                response = await currentClient.GetResponseAsync(chatHistory, toolOptions, cancellationToken);
            }

            string responseText = response.Text.Trim();
            int memoryItemsAfterToolCalls = reActMemory.Count;

            if (string.IsNullOrWhiteSpace(responseText))
            {
                responseText = "No assistant response was returned.";
            }

            chatHistory.Add(new ChatMessage(ChatRole.Assistant, responseText));
            reActMemory.Add("Assistant ReAct response", responseText);
            if (options.Verbose)
            {
                PotatoConsole.WriteModelExchange(iteration, GetLatestModelQuestion(), responseText);
            }

            if (IsFinalResponse(responseText))
            {
                if (RequiresSuccessfulEditBeforeFinal(successfulEditsBeforeExecution))
                {
                    chatHistory.Add(new ChatMessage(
                        ChatRole.User,
                        "You returned FINAL for a project change, but no edit tool has successfully changed a file in this execution. Read the relevant file if needed, then prefer ApplySearchReplaceAsync with exact SEARCH and REPLACE text. Use CreateFileAsync for new files, or ApplyDiffPatchAsync if neither direct edit tool is practical. Do not claim the file was modified until the latest observation confirms a successful edit."));
                    continue;
                }

                PotatoConsole.WriteAgentResponse(RemoveFinalMarker(responseText));
                return;
            }

            int toolCallsThisIteration = agentTools.ToolInvocationCount - toolCallsBefore;
            using (PotatoConsole.StartProgress("Summarizing context..."))
            {
                await reActMemory.SummarizeLargeUnsummarizedItemsAsync(currentOpenAiClient, cancellationToken);
            }
            if (toolCallsThisIteration <= 0)
            {
                if (await TryExecuteTextualActionAsync(responseText, cancellationToken))
                {
                    if (TryCompleteVerifiedDirectReplace(successfulEditsBeforeExecution))
                    {
                        return;
                    }

                    continue;
                }

                if (await TryExecuteDeterministicFallbackAsync(
                        iteration,
                        directCreateFileFallbackAttempted,
                        directReplaceFileFallbackAttempted,
                        cancellationToken))
                {
                    if (TryParseDirectCreateFileRequest(latestUserRequest, out _, out _))
                    {
                        directCreateFileFallbackAttempted = true;
                    }

                    if (TryParseDirectReplaceFileContentRequest(latestUserRequest, out _, out _))
                    {
                        directReplaceFileFallbackAttempted = true;
                    }

                    if (TryCompleteVerifiedDirectReplace(successfulEditsBeforeExecution))
                    {
                        return;
                    }

                    continue;
                }

                if (LooksLikeUnavailableToolClaim(responseText) || LooksLikeShellEditFallbackClaim(responseText))
                {
                    chatHistory.Add(new ChatMessage(
                        ChatRole.User,
                        "Correction: ApplySearchReplaceAsync, CreateFileAsync, and ApplyDiffPatchAsync are available in the CLI. Lack of native model tool visibility is not proof that a CLI tool is unavailable; use textual <tool_call> JSON instead. If you still believe a listed tool is unavailable, name the exact tool and the concrete observation proving it, then continue with one registered textual tool call. Do not use ExecuteShellCommandAsync or manual editor instructions for source edits. The next response must be exactly one registered tool call, preferably ApplySearchReplaceAsync with exact SEARCH and REPLACE text copied from the latest file content, or CreateFileAsync for a new file. " +
                        PromptLibrary.SearchReplaceToolCallExample));
                    continue;
                }

                if (TryReadUserIntervention(responseText, cancellationToken, out string userAnswer))
                {
                    chatHistory.Add(new ChatMessage(
                        ChatRole.User,
                        PromptLibrary.UserInterventionResponseMessage(userAnswer)));
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

            if (options.Verbose)
            {
                PotatoConsole.WriteAgentResponse(responseText);
            }

            if (toolCallsThisIteration > MaxToolCallsPerIteration)
            {
                chatHistory.Add(new ChatMessage(
                    ChatRole.User,
                    $"You used {toolCallsThisIteration} tools in the previous iteration. Finish with FINAL: if the task is complete, otherwise continue with one targeted next action."));
                continue;
            }

            string latestObservation = reActMemory.GetRange(memoryItemsBeforeToolCalls, memoryItemsAfterToolCalls, full: true);
            if (LooksLikeUnavailableToolClaim(responseText))
            {
                latestObservation +=
                    Environment.NewLine +
                    Environment.NewLine +
                    "Correction: The assistant text incorrectly claimed one or more tools are unavailable. Native tool calls just executed successfully in this environment. Lack of native model tool visibility is not proof that a CLI tool is unavailable; use textual <tool_call> JSON instead. If you still believe a listed tool is unavailable, name the exact tool and the concrete observation proving it, then continue with one registered textual tool call. For source edits prefer ApplySearchReplaceAsync with exact SEARCH and REPLACE text; for new files use CreateFileAsync. " +
                    PromptLibrary.SearchReplaceToolCallExample;
            }

            if (TryCompleteVerifiedDirectReplace(successfulEditsBeforeExecution))
            {
                return;
            }

            chatHistory.Add(new ChatMessage(
                ChatRole.User,
                PromptLibrary.NextStepAfterObservationMessage(
                    latestUserRequest ?? string.Empty,
                    Environment.CurrentDirectory,
                    "native tool call",
                    latestObservation)));
        }

        PotatoConsole.WriteError($"Stopped after {MaxReActIterations} ReAct iterations without a FINAL response.");
    }

    private bool RequiresSuccessfulEditBeforeFinal(int successfulEditsBeforeExecution) =>
        ApprovalPolicy.IsProjectChangeRequest(latestUserRequest) &&
        agentTools.SuccessfulEditCount <= successfulEditsBeforeExecution;

    private bool TryCompleteVerifiedDirectReplace(int successfulEditsBeforeExecution)
    {
        if (agentTools.SuccessfulEditCount <= successfulEditsBeforeExecution ||
            !TryParseDirectReplaceFileContentRequest(latestUserRequest, out string filePath, out string expectedContent))
        {
            return false;
        }

        string resolvedPath = ResolveUserFilePath(filePath);
        if (!File.Exists(resolvedPath))
        {
            return false;
        }

        string currentContent = File.ReadAllText(resolvedPath);
        if (!string.Equals(currentContent, expectedContent, StringComparison.Ordinal))
        {
            return false;
        }

        PotatoConsole.WriteAgentResponse($"File content updated and verified: {PathResolver.FormatPathForDisplay(resolvedPath)}");
        return true;
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

    private static bool LooksLikeUserInterventionRequest(string responseText)
    {
        string normalized = responseText.Trim().ToLowerInvariant();
        if (!normalized.Contains("?", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.EndsWith("?", StringComparison.Ordinal) ||
               normalized.Contains("is this approved?", StringComparison.Ordinal) ||
               normalized.Contains("do you approve", StringComparison.Ordinal) ||
               normalized.Contains("should i", StringComparison.Ordinal) ||
               normalized.Contains("do you want", StringComparison.Ordinal) ||
               normalized.Contains("please confirm", StringComparison.Ordinal) ||
               normalized.Contains("need your", StringComparison.Ordinal) ||
               normalized.Contains("which ", StringComparison.Ordinal) ||
               normalized.Contains("what ", StringComparison.Ordinal);
    }

    private bool TryReadUserIntervention(
        string modelQuestion,
        CancellationToken cancellationToken,
        out string userAnswer)
    {
        userAnswer = string.Empty;
        if (!LooksLikeUserInterventionRequest(modelQuestion))
        {
            return false;
        }

        PotatoConsole.WriteStatus("Model requested user input during ReAct execution.");
        PotatoConsole.WriteAgentResponse(modelQuestion);
        userAnswer = PotatoConsole.ReadInterventionInput(cancellationToken);
        AddInputHistory(userAnswer);
        return true;
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

    [GeneratedRegex(@"\A\s*(?:#{1,6}\s*)?(?:\*\*)?\s*FINAL\s*:?\s*(?:\*\*)?", RegexOptions.IgnoreCase)]
    private static partial Regex FinalMarkerRegex();

    private async Task<bool> TryExecuteTextualActionAsync(string responseText, CancellationToken cancellationToken)
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
        using (PotatoConsole.StartProgress("Summarizing context..."))
        {
            await reActMemory.SummarizeLargeUnsummarizedItemsAsync(currentOpenAiClient, cancellationToken);
        }
        chatHistory.Add(new ChatMessage(
            ChatRole.User,
            PromptLibrary.NextStepAfterObservationMessage(
                latestUserRequest ?? string.Empty,
                Environment.CurrentDirectory,
                toolCall.Name,
                result)));
        return true;
    }

    private async Task<bool> TryExecuteDeterministicFallbackAsync(
        int iteration,
        bool directCreateFileFallbackAttempted,
        bool directReplaceFileFallbackAttempted,
        CancellationToken cancellationToken)
    {
        if (!directReplaceFileFallbackAttempted &&
            TryParseDirectReplaceFileContentRequest(latestUserRequest, out string replaceFilePath, out string replacementContent))
        {
            string resolvedPath = ResolveUserFilePath(replaceFilePath);
            if (!File.Exists(resolvedPath))
            {
                return false;
            }

            PotatoConsole.WriteStatus("Model did not call ApplySearchReplaceAsync for a direct file content replacement; running deterministic replacement fallback...");
            string currentContent = await File.ReadAllTextAsync(resolvedPath, cancellationToken);
            string replaceResult = await agentTools.ApplySearchReplaceAsync(resolvedPath, currentContent, replacementContent);
            using (PotatoConsole.StartProgress("Summarizing context..."))
            {
                await reActMemory.SummarizeLargeUnsummarizedItemsAsync(currentOpenAiClient, cancellationToken);
            }

            chatHistory.Add(new ChatMessage(
                ChatRole.User,
                PromptLibrary.NextStepAfterObservationMessage(
                    latestUserRequest ?? string.Empty,
                    Environment.CurrentDirectory,
                    nameof(AgentTools.ApplySearchReplaceAsync),
                    replaceResult)));
            return true;
        }

        if (!directCreateFileFallbackAttempted &&
            TryParseDirectCreateFileRequest(latestUserRequest, out string filePath, out string content))
        {
            PotatoConsole.WriteStatus("Model did not call CreateFileAsync for a direct file creation request; running deterministic file creation fallback...");
            string createResult = await agentTools.CreateFileAsync(filePath, content);
            using (PotatoConsole.StartProgress("Summarizing context..."))
            {
                await reActMemory.SummarizeLargeUnsummarizedItemsAsync(currentOpenAiClient, cancellationToken);
            }

            chatHistory.Add(new ChatMessage(
                ChatRole.User,
                PromptLibrary.NextStepAfterObservationMessage(
                    latestUserRequest ?? string.Empty,
                    Environment.CurrentDirectory,
                    nameof(AgentTools.CreateFileAsync),
                    createResult)));
            return true;
        }

        if (iteration != 1 ||
            !ShouldStartWithProjectInspection(latestUserRequest))
        {
            return false;
        }

        PotatoConsole.WriteStatus("Model did not choose the first project inspection action; running deterministic directory listing fallback...");
        string result = agentTools.ListFiles(Environment.CurrentDirectory);
        using (PotatoConsole.StartProgress("Summarizing context..."))
        {
            await reActMemory.SummarizeLargeUnsummarizedItemsAsync(currentOpenAiClient, cancellationToken);
        }

        chatHistory.Add(new ChatMessage(
            ChatRole.User,
            PromptLibrary.NextStepAfterObservationMessage(
                latestUserRequest ?? string.Empty,
                Environment.CurrentDirectory,
                nameof(AgentTools.ListFiles),
                result)));
        return true;
    }

    private static bool TryParseDirectReplaceFileContentRequest(string? request, out string filePath, out string content)
    {
        filePath = string.Empty;
        content = string.Empty;

        if (string.IsNullOrWhiteSpace(request))
        {
            return false;
        }

        Match match = Regex.Match(
            request.Trim(),
            @"\b(?:change|replace|set|update)\s+(?:the\s+)?content\s+of\s+(?:the\s+)?file\s+[`""']?(?<path>[^\s`""']+)[`""']?\s+to\s+[`""']?(?<content>.+?)[`""']?\s*$",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            match = Regex.Match(
                request.Trim(),
                @"\b(?:change|replace|set|update)\s+(?:the\s+)?file\s+[`""']?(?<path>[^\s`""']+\.[A-Za-z0-9]+)[`""']?\s+(?:content\s+)?to\s+[`""']?(?<content>.+?)[`""']?\s*$",
                RegexOptions.IgnoreCase);
        }

        if (!match.Success)
        {
            return false;
        }

        filePath = match.Groups["path"].Value.Trim();
        content = TrimMatchingQuotes(match.Groups["content"].Value.Trim());
        return !string.IsNullOrWhiteSpace(filePath);
    }

    private static string ResolveUserFilePath(string filePath)
    {
        string trimmed = filePath.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) && uri.IsFile)
        {
            trimmed = uri.LocalPath;
        }

        if (trimmed.StartsWith("~/", StringComparison.Ordinal))
        {
            trimmed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), trimmed[2..]);
        }

        return Path.GetFullPath(Path.IsPathRooted(trimmed)
            ? trimmed
            : Path.Combine(Environment.CurrentDirectory, trimmed));
    }

    private static bool TryParseDirectCreateFileRequest(string? request, out string filePath, out string content)
    {
        filePath = string.Empty;
        content = string.Empty;

        if (string.IsNullOrWhiteSpace(request))
        {
            return false;
        }

        Match match = Regex.Match(
            request.Trim(),
            @"\b(?:write|create|make)\s+(?:a\s+)?file\s+(?:(?:named|called)\s+)?[`""']?(?<path>[^\s`""']+)[`""']?\s+(?:with|containing)\s+(?:the\s+)?content\s+[`""']?(?<content>.+?)[`""']?\s*$",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            match = Regex.Match(
                request.Trim(),
                @"\b(?:write|create|make)\s+[`""']?(?<path>[^\s`""']+\.[A-Za-z0-9]+)[`""']?\s+(?:with|containing)\s+(?:the\s+)?content\s+[`""']?(?<content>.+?)[`""']?\s*$",
                RegexOptions.IgnoreCase);
        }

        if (!match.Success)
        {
            return false;
        }

        filePath = match.Groups["path"].Value.Trim();
        content = TrimMatchingQuotes(match.Groups["content"].Value.Trim());
        return !string.IsNullOrWhiteSpace(filePath);
    }

    private static string TrimMatchingQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'') ||
             (value[0] == '`' && value[^1] == '`')))
        {
            return value[1..^1];
        }

        return value;
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

    private async Task<string> ExecuteTextualToolCallAsync(TextualToolCall toolCall, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string toolName = NormalizeTextualToolName(toolCall.Name);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error executing textual tool call '{toolCall.Name}': {ex.Message}";
        }
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
            _ => normalized
        };
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

    private static TextualToolCall? TryParseSearchReplaceBlock(string responseText)
    {
        TextualToolCall? aiderStyleCall = TryParseAiderSearchReplaceBlock(responseText);
        if (aiderStyleCall is not null)
        {
            return aiderStyleCall;
        }

        TextualToolCall? markdownCall = TryParseMarkdownSearchReplaceBlock(responseText);
        if (markdownCall is not null)
        {
            return markdownCall;
        }

        return TryParseReplaceThisWithThisBlock(responseText);
    }

    private static TextualToolCall? TryParseReplaceThisWithThisBlock(string responseText)
    {
        Match match = Regex.Match(
            responseText,
            @"(?:replace\s+(?:this\s+)?(?:line|code|block)\s*:?)\s*```(?:[^\r\n`]*)?\r?\n(?<search>[\s\S]*?)\r?\n```\s*(?:with\s+(?:this\s+)?(?:animation\s+and\s+message|line|code|block)?\s*:?)\s*```(?:[^\r\n`]*)?\r?\n(?<replace>[\s\S]*?)\r?\n```",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return null;
        }

        string? filePath = TryInferEditFilePath(responseText);
        string search = match.Groups["search"].Value;
        string replace = match.Groups["replace"].Value;
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrEmpty(search))
        {
            return null;
        }

        return new TextualToolCall(
            nameof(AgentTools.ApplySearchReplaceAsync),
            new JsonObject
            {
                ["filePath"] = filePath,
                ["search"] = search,
                ["replace"] = replace
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
        if (string.IsNullOrWhiteSpace(patch) ||
            !patch.Contains("--- ", StringComparison.Ordinal) ||
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

    private static bool LooksLikeUnavailableToolClaim(string responseText)
    {
        string normalized = responseText.ToLowerInvariant();
        return normalized.Contains("tool", StringComparison.Ordinal) &&
               (normalized.Contains("not available", StringComparison.Ordinal) ||
                normalized.Contains("unavailable", StringComparison.Ordinal) ||
                normalized.Contains("missing from the available", StringComparison.Ordinal) ||
                normalized.Contains("missing from the current", StringComparison.Ordinal));
    }

    private static bool LooksLikeShellEditFallbackClaim(string responseText)
    {
        string normalized = responseText.ToLowerInvariant();
        return normalized.Contains("executeshellcommandasync", StringComparison.Ordinal) &&
               (normalized.Contains("fallback", StringComparison.Ordinal) ||
                normalized.Contains("manually inject", StringComparison.Ordinal) ||
                normalized.Contains("manual edit", StringComparison.Ordinal) ||
                normalized.Contains("edit", StringComparison.Ordinal));
    }

    private static TextualToolCall? TryParseAiderSearchReplaceBlock(string responseText)
    {
        Match match = Regex.Match(
            responseText,
            @"(?<path>^[^\r\n<>`]+?)\s*\r?\n<<<<<<< SEARCH\r?\n(?<search>[\s\S]*?)\r?\n=======\r?\n(?<replace>[\s\S]*?)\r?\n>>>>>>> REPLACE",
            RegexOptions.Multiline);

        if (!match.Success)
        {
            return null;
        }

        string filePath = match.Groups["path"].Value.Trim();
        string search = match.Groups["search"].Value;
        string replace = match.Groups["replace"].Value;
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrEmpty(search))
        {
            return null;
        }

        return new TextualToolCall(
            nameof(AgentTools.ApplySearchReplaceAsync),
            new JsonObject
            {
                ["filePath"] = filePath,
                ["search"] = search,
                ["replace"] = replace
            });
    }

    private static TextualToolCall? TryParseMarkdownSearchReplaceBlock(string responseText)
    {
        Match match = Regex.Match(
            responseText,
            @"\*\*SEARCH\*\*\s*:?\s*```(?:[^\r\n`]*)?\r?\n(?<search>[\s\S]*?)\r?\n```\s*\*\*REPLACE\*\*\s*:?\s*```(?:[^\r\n`]*)?\r?\n(?<replace>[\s\S]*?)\r?\n```",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return null;
        }

        string? filePath = TryInferEditFilePath(responseText);
        string search = match.Groups["search"].Value;
        string replace = match.Groups["replace"].Value;
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrEmpty(search))
        {
            return null;
        }

        return new TextualToolCall(
            nameof(AgentTools.ApplySearchReplaceAsync),
            new JsonObject
            {
                ["filePath"] = filePath,
                ["search"] = search,
                ["replace"] = replace
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

            ChatResponse greeting;
            using (PotatoConsole.StartProgress("Loading welcome message..."))
            {
                greeting = await currentClient.GetResponseAsync(greetingMessages, new ChatOptions());
            }

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

    private void SetUseCompiledDefaultPrompts(bool useCompiledDefaultsOnly)
    {
        appSettingsStore.SetUseCompiledDefaultPrompts(useCompiledDefaultsOnly);
        PromptLibrary.SetUseCompiledDefaultsOnly(useCompiledDefaultsOnly);
        ResetConversationState();
        chatHistory[0] = new ChatMessage(ChatRole.System, PromptLibrary.SystemPrompt);
    }

    private CancellationTokenSource BeginTaskCancellation()
    {
        var cancellationSource = new CancellationTokenSource();
        lock (taskCancellationLock)
        {
            currentTaskCancellationSource = cancellationSource;
            agentTools.CurrentCancellationToken = cancellationSource.Token;
        }

        return cancellationSource;
    }

    private void EndTaskCancellation(CancellationTokenSource cancellationSource)
    {
        lock (taskCancellationLock)
        {
            if (ReferenceEquals(currentTaskCancellationSource, cancellationSource))
            {
                currentTaskCancellationSource = null;
                agentTools.CurrentCancellationToken = default;
            }
        }
    }

    private void HandleConsoleCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        CancellationTokenSource? cancellationSource;
        lock (taskCancellationLock)
        {
            cancellationSource = currentTaskCancellationSource;
        }

        if (cancellationSource is null)
        {
            return;
        }

        eventArgs.Cancel = true;
        cancellationSource.Cancel();
        PotatoConsole.WriteStatus("Abort requested. Cancelling current task...");
    }
}
