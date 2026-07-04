using Microsoft.Extensions.AI;

internal sealed class PotatoSession(
    Uri gladosEndpoint,
    IChatClient openAiClient,
    IChatClient client,
    GladosChatClientFactory clientFactory,
    ModelSelector modelSelector)
{
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
        ShellCommandPlan? directShellCommand = null;
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
                directShellCommand = await PlanExecutionAsync();

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
                _ => "Agent is executing..."
            });

            if (directShellCommand is not null)
            {
                await ExecuteDirectShellCommandAsync(directShellCommand);
                return;
            }

            var currentOptions = new ChatOptions
            {
                Tools = currentState == AgentState.Confirmed ? toolOptions.Tools : null
            };

            ChatResponse response = await currentClient.GetResponseAsync(chatHistory, currentOptions);
            chatHistory.Add(new ChatMessage(ChatRole.Assistant, response.Text));
            StoreLatestResponse(response.Text);
            PotatoConsole.WriteAgentResponse(response.Text);

            if (currentState == AgentState.Approaching &&
                !ApprovalPolicy.RequiresExplicitExecutionApproval(latestSpecification, latestApproach))
            {
                directShellCommand = await PlanExecutionAsync();
                if (directShellCommand is not null)
                {
                    currentState = AgentState.Confirmed;
                    PotatoConsole.WriteStatus("Proceeding to command permission...");
                    await ExecuteDirectShellCommandAsync(directShellCommand);
                }
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
                AIFunctionFactory.Create(agentTools.ExecuteShellCommandAsync)
            ]
        };
    }

    private async Task ExecuteDirectShellCommandAsync(ShellCommandPlan directShellCommand)
    {
        string directResult = await agentTools.ExecuteShellCommandAsync(
            directShellCommand.Command,
            directShellCommand.WorkingDirectory,
            directShellCommand.TimeoutSeconds);

        chatHistory.Add(new ChatMessage(ChatRole.Assistant, directResult));
        PotatoConsole.WriteAgentResponse(directResult);
        ResetConversationState();
    }

    private Task<ShellCommandPlan?> PlanExecutionAsync()
    {
        var planner = new ExecutionPlanner(currentOpenAiClient);
        return planner.TryPlanExecutionAsync(latestUserRequest, latestSpecification, latestApproach);
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
