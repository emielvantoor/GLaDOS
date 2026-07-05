using Microsoft.Extensions.AI;

internal sealed class SlashCommandHandler(
    Uri gladosEndpoint,
    GladosChatClientFactory clientFactory,
    ModelSelector modelSelector,
    FileMentionExpander fileMentionExpander,
    Action resetConversationState,
    Action writeConversationTranscript,
    Func<IChatClient> getClient,
    Action<string, IChatClient, IChatClient> switchModel)
{
    public async Task<bool> TryHandleAsync(string input)
    {
        string trimmed = input.Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        string command = trimmed;
        string arguments = string.Empty;
        int splitIndex = trimmed.IndexOf(' ');
        if (splitIndex >= 0)
        {
            command = trimmed[..splitIndex];
            arguments = trimmed[(splitIndex + 1)..].Trim();
        }

        switch (command.ToLowerInvariant())
        {
            case "/model":
                await HandleModelCommandAsync();
                return true;

            case "/cd":
                HandleChangeDirectoryCommand(arguments);
                return true;

            case "/ask":
                await HandleSideQuestionCommandAsync(arguments);
                return true;

            case "/transcript":
                writeConversationTranscript();
                return true;

            case "/abort":
                resetConversationState();
                PotatoConsole.WriteSuccess("Aborted current task. Back at the main prompt.");
                return true;

            default:
                PotatoConsole.WriteError($"Unknown command: {command}");
                Console.WriteLine("Type ? for shortcuts.");
                return true;
        }
    }

    private async Task HandleModelCommandAsync()
    {
        string selectedModel = await modelSelector.PromptForModelAsync(gladosEndpoint);
        IChatClient selectedOpenAiClient = clientFactory.CreateOpenAiClient(gladosEndpoint, selectedModel);
        IChatClient selectedClient = clientFactory.CreateFunctionClient(selectedOpenAiClient);

        switchModel(selectedModel, selectedOpenAiClient, selectedClient);
        PotatoConsole.WriteSuccess($"Selected model: {selectedModel}");
    }

    private static void HandleChangeDirectoryCommand(string arguments)
    {
        string rawPath = string.IsNullOrWhiteSpace(arguments)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : arguments.Trim().Trim('"', '\'');

        string? resolvedPath = PathResolver.ResolveMentionedPath(rawPath);
        if (resolvedPath is null)
        {
            PotatoConsole.WriteError("Could not resolve working directory.");
            return;
        }

        if (File.Exists(resolvedPath))
        {
            resolvedPath = Path.GetDirectoryName(resolvedPath);
        }

        if (string.IsNullOrWhiteSpace(resolvedPath) || !Directory.Exists(resolvedPath))
        {
            PotatoConsole.WriteError($"Directory not found: {resolvedPath}");
            return;
        }

        Environment.CurrentDirectory = resolvedPath;
        PotatoConsole.WriteSuccess($"Working directory: {PathResolver.FormatPathForDisplay(Environment.CurrentDirectory)}");
    }

    private async Task HandleSideQuestionCommandAsync(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            Console.Write("Side question: ");
            question = Console.ReadLine() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            PotatoConsole.WriteError("No side question was provided.");
            return;
        }

        string expandedQuestion = fileMentionExpander.Expand(question);
        var sideQuestionMessages = new List<ChatMessage>
        {
            new(ChatRole.System, PromptLibrary.SideQuestionSystemPrompt),
            new(ChatRole.User, expandedQuestion)
        };

        try
        {
            PotatoConsole.WriteStatus("Answering side question...");
            ChatResponse response = await getClient().GetResponseAsync(sideQuestionMessages, new ChatOptions());
            PotatoConsole.WriteAgentResponse(response.Text);
        }
        catch (Exception ex)
        {
            PotatoConsole.WriteError($"Side question failed: {ex.Message}");
        }
    }
}
