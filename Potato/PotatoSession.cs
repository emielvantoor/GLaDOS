using Microsoft.Extensions.AI;

internal sealed class PotatoSession(
    Uri gladosEndpoint,
    IChatClient openAiClient,
    IChatClient client,
    GladosChatClientFactory clientFactory,
    ModelSelector modelSelector)
{
    private const int MaxReActIterations = 12;
    private const int MaxToolCallsPerIteration = 8;

    private readonly AgentTools agentTools = new();
    private readonly FileMentionExpander fileMentionExpander = new();
    private readonly List<ChatMessage> chatHistory =
    [
        new(ChatRole.System, PromptLibrary.SystemPrompt)
    ];

    private AgentState currentState = AgentState.Specifying;
    private IChatClient currentOpenAiClient = openAiClient;
    private IChatClient currentClient = client;
    private string? latestSpecification;
    private string? latestApproach;
    private string? latestUserRequest;

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
            () => currentClient,
            SwitchModel);

        while (true)
        {
            PotatoConsole.WritePrompt();
            string? userInput = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(userInput))
            {
                continue;
            }

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

    private async Task HandleUserInputAsync(string userInput, ChatOptions toolOptions)
    {
        string messageForModel = fileMentionExpander.Expand(userInput);

        if (currentState == AgentState.Specifying)
        {
            if (ApprovalPolicy.IsUserApproval(userInput))
            {
                currentState = AgentState.Approaching;
                chatHistory.Add(new ChatMessage(ChatRole.User, PromptLibrary.ApprovalToApproachMessage(latestSpecification)));
            }
            else
            {
                latestUserRequest = messageForModel;
                chatHistory.Add(new ChatMessage(ChatRole.User, messageForModel));
            }
        }
        else if (currentState == AgentState.Approaching)
        {
            if (ApprovalPolicy.IsUserExecutionApproval(userInput))
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
                !ApprovalPolicy.RequiresExplicitExecutionApproval(latestSpecification, latestApproach))
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

            ChatResponse response = await currentClient.GetResponseAsync(chatHistory, toolOptions);
            string responseText = response.Text.Trim();

            if (string.IsNullOrWhiteSpace(responseText))
            {
                responseText = "No assistant response was returned.";
            }

            chatHistory.Add(new ChatMessage(ChatRole.Assistant, responseText));
            PotatoConsole.WriteAgentResponse(RemoveFinalMarker(responseText));

            if (IsFinalResponse(responseText))
            {
                return;
            }

            int toolCallsThisIteration = agentTools.ToolInvocationCount - toolCallsBefore;
            if (toolCallsThisIteration <= 0)
            {
                chatHistory.Add(new ChatMessage(
                    ChatRole.User,
                    PromptLibrary.ContinueReActMessage(requireToolUse: false)));
                continue;
            }

            if (toolCallsThisIteration > MaxToolCallsPerIteration)
            {
                chatHistory.Add(new ChatMessage(
                    ChatRole.User,
                    $"You used {toolCallsThisIteration} tools in the previous iteration. Finish with FINAL: if the task is complete, otherwise continue with one targeted next action."));
                continue;
            }

            chatHistory.Add(new ChatMessage(
                ChatRole.User,
                PromptLibrary.ContinueReActMessage(requireToolUse: true)));
        }

        PotatoConsole.WriteError($"Stopped after {MaxReActIterations} ReAct iterations without a FINAL response.");
    }

    private static bool IsFinalResponse(string responseText) =>
        responseText.TrimStart().StartsWith("FINAL:", StringComparison.OrdinalIgnoreCase);

    private static string RemoveFinalMarker(string responseText)
    {
        string trimmed = responseText.TrimStart();
        return trimmed.StartsWith("FINAL:", StringComparison.OrdinalIgnoreCase)
            ? trimmed["FINAL:".Length..].TrimStart()
            : responseText;
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
        latestSpecification = null;
        latestApproach = null;
        latestUserRequest = null;
        if (chatHistory.Count > 1)
        {
            chatHistory.RemoveRange(1, chatHistory.Count - 1);
        }
    }
}
