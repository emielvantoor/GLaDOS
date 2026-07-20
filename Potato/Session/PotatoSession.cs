using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Potato.Tools;

namespace Potato.Session;

internal sealed class PotatoSession
{
    private readonly Uri gladosEndpoint;
    private readonly GladosChatClientFactory clientFactory;
    private readonly ModelSelector modelSelector;
    private readonly ExecutionMemory executionMemory;
    private readonly AgentTools agentTools;
    private readonly FileMentionExpander fileMentionExpander = new();
    private readonly PotatoRuntimeOptions options;
    private readonly PotatoAppSettingsStore appSettingsStore;
    private readonly CurrentChatClientState chatClientState;
    private readonly PlanningService planningService;
    private readonly ReActSession _reActSession;
    private readonly List<string> inputHistory = [];
    private readonly List<SessionTranscript> archivedSessions = [];
    private readonly List<ChatMessage> chatHistory = [];
    private readonly object taskCancellationLock = new();

    private IChatClient currentOpenAiClient;
    private int nextSessionNumber = 1;
    private int currentSessionNumber;
    private string? currentSessionSubject;
    private DateTime currentSessionStartedAt;
    private CancellationTokenSource? currentTaskCancellationSource;
    private bool contextOptimizationEnabled;

    public PotatoSession(
        Uri gladosEndpoint,
        IChatClient openAiClient,
        GladosChatClientFactory clientFactory,
        ModelSelector modelSelector,
        PotatoRuntimeOptions options,
        PotatoAppSettingsStore appSettingsStore,
        AgentTools agentTools,
        ExecutionMemory executionMemory,
        CurrentChatClientState chatClientState,
        PlanningService planningService,
        ReActSession reActSession)
    {
        this.gladosEndpoint = gladosEndpoint;
        this.clientFactory = clientFactory;
        this.modelSelector = modelSelector;
        this.options = options;
        this.appSettingsStore = appSettingsStore;
        this.agentTools = agentTools;
        this.executionMemory = executionMemory;
        this.chatClientState = chatClientState;
        this.planningService = planningService;
        _reActSession = reActSession;
        currentOpenAiClient = openAiClient;
        
        PotatoAppSettings settings = appSettingsStore.Load();
        contextOptimizationEnabled = settings.ContextOptimizationEnabled ?? false;
    }

    public async Task RunAsync()
    {
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
            appSettingsStore.SetWebUiInputEnabled,
            appSettingsStore.SetSelectedModel,
            HandleTranscriptCommand,
            WriteSessions,
            ContinueSession,
            () => options.ContextSize,
            () => currentOpenAiClient,
            SwitchModel,
            SetContextOptimizationEnabled,
            GetContextOptimizationEnabled);

        try
        {
            while (true)
            {
                string? userInput = PotatoConsole.ReadPromptInput(inputHistory, "Type a goal or @path/to/file");

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
                    continue;
                }

                await HandleUserGoalAsync(userInput);
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

    private async Task HandleUserGoalAsync(string userInput)
    {
        using CancellationTokenSource taskCancellationSource = BeginTaskCancellation();
        CancellationToken cancellationToken = taskCancellationSource.Token;
        string expandedGoal = fileMentionExpander.Expand(userInput);
        if (IsNonActionableUserInput(userInput))
        {
            PotatoConsole.WriteAgentResponse("How can I help?");
            return;
        }

        EnsureCurrentSession(userInput);
        chatHistory.Add(new ChatMessage(ChatRole.User, expandedGoal));
        PotatoConsole.EventSink?.Record("message", "user", expandedGoal, collapsed: false);
        string contextualGoal = BuildContextualGoal(expandedGoal);

        try
        {
            await HandleUserGoalWithReActAsync(contextualGoal, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ResetConversationState();
            PotatoConsole.WriteSuccess("Aborted current task. Back at the main prompt.");
        }
        catch (Exception ex)
        {
            PotatoConsole.WriteError($"Error: {ex.Message}");
            ResetConversationState();
        }
        finally
        {
            EndTaskCancellation(taskCancellationSource);
        }
    }

    private async Task HandleUserGoalWithReActAsync(string expandedGoal, CancellationToken cancellationToken)
    {
        string guidance = planningService.BuildDirectExecutionGuidance(expandedGoal, Environment.CurrentDirectory);
        string finalMessage = await _reActSession.ExecuteAsync(expandedGoal, guidance, currentOpenAiClient, GetContextOptimizationEnabled, cancellationToken);
        chatHistory.Add(new ChatMessage(ChatRole.Assistant, finalMessage));
        PotatoConsole.WriteAgentResponse(finalMessage);
        ResetConversationState();
    }

    private static bool IsNonActionableUserInput(string input)
    {
        string normalized = input.Trim().Trim('.', '!', '?').ToLowerInvariant();
        return normalized is "test" or "testing" or "ping" or "hello" or "hi" or "hey";
    }

    private string BuildContextualGoal(string currentGoal)
    {
        IReadOnlyList<ChatMessage> priorMessages = chatHistory.Count > 0
            ? chatHistory.Take(chatHistory.Count - 1).ToArray()
            : [];

        if (priorMessages.Count == 0)
        {
            return currentGoal;
        }

        return $"""
        Current request:
        {currentGoal}

        Prior chat context from this Potato session:
        {FormatConversationContext(priorMessages)}

        Use the prior chat context to understand references like "continue", "same file", "that change", or follow-up corrections. The current request is authoritative if it conflicts with earlier context.
        """;
    }

    private static string FormatConversationContext(IReadOnlyList<ChatMessage> messages)
    {
        const int maxContextCharacters = 20_000;
        var builder = new StringBuilder();

        for (int i = messages.Count - 1; i >= 0; i--)
        {
            ChatMessage message = messages[i];
            string text = string.IsNullOrWhiteSpace(message.Text) ? "(empty)" : message.Text.Trim();
            string entry = $"{message.Role}: {text}\n\n";
            if (builder.Length + entry.Length > maxContextCharacters)
            {
                break;
            }

            builder.Insert(0, entry);
        }

        return builder.ToString().TrimEnd();
    }

    // private static string SelectTargetCodeBlock(string fileContent, string patchArgument)
    // {
    //     string[] lines = fileContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    //     string[] candidates = ExtractIdentifierCandidates(patchArgument);
    //
    //     foreach (string candidate in candidates)
    //     {
    //         for (int i = 0; i < lines.Length; i++)
    //         {
    //             if (!Regex.IsMatch(lines[i], $@"\b{Regex.Escape(candidate)}\b"))
    //             {
    //                 continue;
    //             }
    //
    //             return SliceLines(lines, Math.Max(0, i - 25), Math.Min(lines.Length, i + 120));
    //         }
    //     }
    //
    //     const int maxPatchContextCharacters = 18_000;
    //     if (fileContent.Length <= maxPatchContextCharacters)
    //     {
    //         return fileContent;
    //     }
    //
    //     return fileContent[..maxPatchContextCharacters];
    // }

    // private static string[] ExtractIdentifierCandidates(string text) =>
    //     Regex.Matches(text, @"[A-Za-z_][A-Za-z0-9_]{2,}")
    //         .Select(match => match.Value)
    //         .Where(value => !CommonPlannerWords.Contains(value, StringComparer.OrdinalIgnoreCase))
    //         .Distinct(StringComparer.Ordinal)
    //         .ToArray();

    // private static readonly string[] CommonPlannerWords =
    // [
    //     "the", "and", "for", "with", "from", "into", "replace", "update", "modify", "refactor",
    //     "method", "class", "file", "code", "patch", "change", "implementation", "logic"
    // ];

    // private static string SliceLines(string[] lines, int start, int end)
    // {
    //     var builder = new StringBuilder();
    //     for (int i = start; i < end; i++)
    //     {
    //         builder.AppendLine(lines[i]);
    //     }
    //
    //     return builder.ToString();
    // }


    private async Task WriteUntrackedGreetingAsync()
    {
        try
        {
            var greetingMessages = new List<ChatMessage>
            {
                new(ChatRole.System, Potato.Prompts.PromptLibrary.GreetingSystemPrompt),
                new(ChatRole.User, Potato.Prompts.PromptLibrary.GreetingUserPrompt)
            };

            ChatResponse greeting;
            using (PotatoConsole.StartProgress("Loading welcome message..."))
            {
                greeting = await currentOpenAiClient.GetResponseAsync(greetingMessages,
                    new ChatOptions { Temperature = 0.7f });
            }

            PotatoConsole.WriteAgentResponse(greeting.Text);
        }
        catch (Exception ex)
        {
            PotatoConsole.WriteStatus($"Skipping startup greeting: {ex.Message}");
        }
    }

    private void SwitchModel(IChatClient selectedOpenAiClient, string selectedModel)
    {
        currentOpenAiClient = selectedOpenAiClient;
        chatClientState.SetOpenAiClient(selectedOpenAiClient);
        chatClientState.SetModel(selectedModel);
    }

    private void EnsureCurrentSession(string firstUserInput)
    {
        if (currentSessionNumber != 0)
        {
            return;
        }

        currentSessionNumber = nextSessionNumber++;
        currentSessionSubject = BuildSessionSubject(firstUserInput);
        currentSessionStartedAt = DateTime.Now;
    }

    private void ArchiveCurrentSession()
    {
        if (currentSessionNumber == 0 || chatHistory.Count == 0)
        {
            currentSessionNumber = 0;
            currentSessionSubject = null;
            return;
        }

        archivedSessions.Add(new SessionTranscript(
            currentSessionNumber,
            currentSessionSubject ?? $"Session {currentSessionNumber}",
            currentSessionStartedAt,
            chatHistory.Select(CloneMessage).ToList()));

        currentSessionNumber = 0;
        currentSessionSubject = null;
    }

    private void WriteSessions()
    {
        if (currentSessionNumber == 0 && archivedSessions.Count == 0)
        {
            PotatoConsole.WriteStatus("No tracked sessions yet.");
            return;
        }

        foreach (SessionTranscript session in archivedSessions)
        {
            PotatoConsole.WriteStatus(
                $"{session.Number}: {session.Subject} ({session.StartedAt:g}, {session.Messages.Count} messages)");
        }

        if (currentSessionNumber != 0)
        {
            PotatoConsole.WriteStatus(
                $"{currentSessionNumber}: {currentSessionSubject} ({currentSessionStartedAt:g}, {chatHistory.Count} messages, current)");
        }
    }

    private void ContinueSession(string arguments)
    {
        int sessionNumber = ParseContinueSessionSelector(arguments);
        if (sessionNumber == 0)
        {
            PotatoConsole.WriteError("No matching session. Type /sessions to list tracked sessions.");
            return;
        }

        if (currentSessionNumber == sessionNumber)
        {
            PotatoConsole.WriteStatus($"Session {sessionNumber} is already current.");
            return;
        }

        int archivedIndex = archivedSessions.FindIndex(candidate => candidate.Number == sessionNumber);
        if (archivedIndex < 0)
        {
            PotatoConsole.WriteError("Only archived sessions can be continued. Type /sessions to list tracked sessions.");
            return;
        }

        SessionTranscript session = archivedSessions[archivedIndex];
        ArchiveCurrentSession();
        archivedSessions.RemoveAt(archivedIndex);

        currentSessionNumber = session.Number;
        currentSessionSubject = session.Subject;
        currentSessionStartedAt = session.StartedAt;
        chatHistory.Clear();
        chatHistory.AddRange(session.Messages.Select(CloneMessage));
        executionMemory.Clear();

        PotatoConsole.WriteSuccess(
            $"Continuing session {session.Number}: {session.Subject} ({chatHistory.Count} messages).");
    }

    private int ParseContinueSessionSelector(string arguments)
    {
        string selector = arguments.Trim();
        if (string.IsNullOrWhiteSpace(selector) || selector.Equals("last", StringComparison.OrdinalIgnoreCase) ||
            selector.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return archivedSessions.Count > 0 ? archivedSessions[^1].Number : currentSessionNumber;
        }

        return ParseSessionSelector(selector);
    }

    private void HandleTranscriptCommand(string arguments)
    {
        string trimmed = arguments.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            ShowTranscript(currentSessionNumber);
            return;
        }

        string[] parts = trimmed.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        string action = parts[0].ToLowerInvariant();

        if (action is "show" or "view")
        {
            int sessionNumber = parts.Length >= 2 ? ParseSessionSelector(parts[1]) : currentSessionNumber;
            ShowTranscript(sessionNumber);
            return;
        }

        if (action is "save" or "write" or "export")
        {
            int sessionNumber = currentSessionNumber;
            string? path = null;

            if (parts.Length >= 2)
            {
                int parsedSelector = ParseSessionSelector(parts[1]);
                if (parsedSelector != 0)
                {
                    sessionNumber = parsedSelector;
                    path = parts.Length >= 3 ? parts[2] : null;
                }
                else
                {
                    path = trimmed[(parts[0].Length + 1)..].Trim();
                }
            }

            SaveTranscript(sessionNumber, path);
            return;
        }

        if (int.TryParse(parts[0], out int selectedSessionNumber))
        {
            ShowTranscript(selectedSessionNumber);
            return;
        }

        SaveTranscript(currentSessionNumber, trimmed);
    }

    private int ParseSessionSelector(string selector)
    {
        string trimmed = selector.Trim();
        if (trimmed.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            return currentSessionNumber;
        }

        return int.TryParse(trimmed, out int sessionNumber) ? sessionNumber : 0;
    }

    private void ShowTranscript(int sessionNumber)
    {
        if (!TryGetSession(sessionNumber, out SessionTranscript? session))
        {
            PotatoConsole.WriteError("No matching session. Type /sessions to list tracked sessions.");
            return;
        }

        PotatoConsole.WriteConversationTranscript(
            $"Session {session.Number}: {session.Subject}",
            session.Messages);
    }

    private void SaveTranscript(int sessionNumber, string? path)
    {
        if (!TryGetSession(sessionNumber, out SessionTranscript? session))
        {
            PotatoConsole.WriteError("No matching session. Type /sessions to list tracked sessions.");
            return;
        }

        string resolvedPath = ResolveTranscriptPath(path, session);
        string? directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(resolvedPath, FormatTranscript(session));
        PotatoConsole.WriteSuccess($"Transcript saved: {PathResolver.FormatPathForDisplay(resolvedPath)}");
    }

    private bool TryGetSession(int sessionNumber, out SessionTranscript session)
    {
        if (sessionNumber == 0)
        {
            session = null!;
            return false;
        }

        if (currentSessionNumber == sessionNumber)
        {
            session = new SessionTranscript(
                currentSessionNumber,
                currentSessionSubject ?? $"Session {currentSessionNumber}",
                currentSessionStartedAt,
                chatHistory.Select(CloneMessage).ToList());
            return true;
        }

        SessionTranscript? archivedSession =
            archivedSessions.FirstOrDefault(candidate => candidate.Number == sessionNumber);
        if (archivedSession is null)
        {
            session = null!;
            return false;
        }

        session = archivedSession;
        return true;
    }

    private static ChatMessage CloneMessage(ChatMessage message) =>
        new(message.Role, message.Text);

    private static string FormatTranscript(SessionTranscript session)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Session: {session.Number}");
        builder.AppendLine($"Subject: {session.Subject}");
        builder.AppendLine($"Started: {session.StartedAt:O}");
        builder.AppendLine();

        for (int i = 0; i < session.Messages.Count; i++)
        {
            ChatMessage message = session.Messages[i];
            builder.AppendLine($"## {i + 1}. {message.Role}");
            builder.AppendLine(string.IsNullOrWhiteSpace(message.Text) ? "(empty)" : message.Text);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string ResolveTranscriptPath(string? path, SessionTranscript session)
    {
        string rawPath = string.IsNullOrWhiteSpace(path)
            ? BuildDefaultTranscriptFileName(session)
            : path.Trim().Trim('"', '\'');

        if (Uri.TryCreate(rawPath, UriKind.Absolute, out Uri? uri) && uri.IsFile)
        {
            rawPath = uri.LocalPath;
        }

        if (rawPath.StartsWith("~/", StringComparison.Ordinal))
        {
            rawPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), rawPath[2..]);
        }

        string resolvedPath = Path.GetFullPath(Path.IsPathRooted(rawPath)
            ? rawPath
            : Path.Combine(Environment.CurrentDirectory, rawPath));

        if (Directory.Exists(resolvedPath))
        {
            resolvedPath = Path.Combine(resolvedPath, BuildDefaultTranscriptFileName(session));
        }

        return resolvedPath;
    }

    private static string BuildDefaultTranscriptFileName(SessionTranscript session) =>
        $"potato-session-{session.Number:000}-{Slugify(session.Subject)}.txt";

    private static string BuildSessionSubject(string input)
    {
        string text = Regex.Replace(input.Trim(), @"\s+", " ");
        if (text.Length > 80)
        {
            text = text[..80].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(text) ? "Untitled session" : text;
    }

    private static string Slugify(string text)
    {
        string slug = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 48)
        {
            slug = slug[..48].Trim('-');
        }

        return string.IsNullOrWhiteSpace(slug) ? "untitled" : slug;
    }

    private void ResetConversationState()
    {
        ArchiveCurrentSession();
        executionMemory.Clear();
        chatHistory.Clear();
    }

    private void SetUseCompiledDefaultPrompts(bool useCompiledDefaultsOnly)
    {
        appSettingsStore.SetUseCompiledDefaultPrompts(useCompiledDefaultsOnly);
        Potato.Prompts.PromptLibrary.SetUseCompiledDefaultsOnly(useCompiledDefaultsOnly);
        ResetConversationState();
    }

    private void SetContextOptimizationEnabled(bool enabled)
    {
        contextOptimizationEnabled = enabled;
        appSettingsStore.SetContextOptimizationEnabled(enabled);
    }

    private bool GetContextOptimizationEnabled() => contextOptimizationEnabled;

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


    private sealed record SessionTranscript(
        int Number,
        string Subject,
        DateTime StartedAt,
        IReadOnlyList<ChatMessage> Messages);

}
