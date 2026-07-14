using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Session.Tasks;
using Potato.Tools;

namespace Potato.Session;

internal sealed class PipelineSession
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
    private readonly PlanningService _planningService;
    private readonly ExecutionService _executionService;
    private readonly ReActSession _reActSession;
    private readonly List<string> inputHistory = [];
    private readonly List<SessionTranscript> archivedSessions = [];
    private readonly List<ChatMessage> chatHistory = [];
    private readonly object taskCancellationLock = new();

    private IChatClient currentOpenAiClient;
    private ExecutionMode executionMode;
    private int nextSessionNumber = 1;
    private int currentSessionNumber;
    private string? currentSessionSubject;
    private DateTime currentSessionStartedAt;
    private CancellationTokenSource? currentTaskCancellationSource;

    public PipelineSession(
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
        ExecutionService executionService,
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
        _planningService = planningService;
        _executionService = executionService;
        _reActSession = reActSession;
        currentOpenAiClient = openAiClient;
        executionMode = ParseExecutionMode(options.ExecutionMode);
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
            appSettingsStore.SetSelectedModel,
            HandleTranscriptCommand,
            WriteSessions,
            ContinueSession,
            GetExecutionMode,
            SetExecutionMode,
            () => currentOpenAiClient,
            SwitchModel);

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
            if (executionMode == ExecutionMode.ReAct)
            {
                await HandleUserGoalWithReActAsync(contextualGoal, cancellationToken);
                return;
            }

            List<AgentTask>? plan = await ReviewPlanAsync(contextualGoal, currentOpenAiClient, cancellationToken);
            if (plan is null)
            {
                string abortedMessage = "Plan was aborted. No execution was started.";
                chatHistory.Add(new ChatMessage(ChatRole.Assistant, abortedMessage));
                PotatoConsole.WriteStatus(abortedMessage);
                ResetConversationState();
                return;
            }

            ExecutionResult result = await _executionService.ExecutePlanAsync(contextualGoal, plan, currentOpenAiClient, cancellationToken);
            string finalMessage = result.Success
                ? BuildSuccessSummary(result)
                : BuildFailureSummary(result);

            chatHistory.Add(new ChatMessage(ChatRole.Assistant, finalMessage));
            PotatoConsole.WriteAgentResponse(finalMessage);
            ResetConversationState();
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
        string guidance = _planningService.BuildDirectExecutionGuidance(expandedGoal, Environment.CurrentDirectory);
        string finalMessage = await _reActSession.ExecuteAsync(expandedGoal, guidance, currentOpenAiClient, cancellationToken);
        chatHistory.Add(new ChatMessage(ChatRole.Assistant, finalMessage));
        PotatoConsole.WriteAgentResponse(finalMessage);
        ResetConversationState();
    }

    private async Task<List<AgentTask>?> ReviewPlanAsync(
        string expandedGoal,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        string planningGoal = expandedGoal;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<AgentTask> plan = await _planningService.PlanAsync(planningGoal, chatClient, cancellationToken);
            string formattedPlan = FormatTaskList(plan);
            chatHistory.Add(new ChatMessage(ChatRole.Assistant, formattedPlan));
            PotatoConsole.WriteAgentResponse(formattedPlan);
            PotatoConsole.WriteStatus("Review plan: type execute/yes to run, type changes to re-plan, or abort to cancel.");

            string? reviewInput = PotatoConsole.ReadPromptInput(inputHistory, "execute, changes, or abort", cancellationToken);
            if (string.IsNullOrWhiteSpace(reviewInput))
            {
                continue;
            }

            AddInputHistory(reviewInput);
            string trimmed = reviewInput.Trim();
            if (IsAbortInput(trimmed))
            {
                return null;
            }

            if (ApprovalPolicy.IsUserExecutionApproval(trimmed))
            {
                return plan;
            }

            planningGoal = BuildReplanGoal(expandedGoal, formattedPlan, trimmed);
            chatHistory.Add(new ChatMessage(ChatRole.User, $"Plan correction: {trimmed}"));
            PotatoConsole.EventSink?.Record("message", "user", $"Plan correction: {trimmed}", collapsed: false);
        }
    }

    private static bool IsAbortInput(string input)
    {
        string normalized = input.Trim().Trim('.', '!', '?').ToLowerInvariant();
        return normalized is "abort" or "cancel" or "stop" or "no" or "n";
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

    private static string BuildReplanGoal(string originalGoal, string previousPlan, string correction) =>
        $"""
        Original request:
        {originalGoal}

        Previous plan that the user rejected:
        {previousPlan}

        User correction for the next plan:
        {correction}

        Create a new deterministic plan that follows the original request and the user correction. Do not repeat rejected target files or steps unless the correction explicitly asks for them.
        """;

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


    private static string BuildSuccessSummary(ExecutionResult result)
    {
        TaskObservation? userFacingObservation = result.Observations.LastOrDefault(observation =>
            StringHelper.NormalizeAction(observation.Action) is "write-report");
        if (userFacingObservation is not null)
        {
            return userFacingObservation.Result;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Execution completed.");
        foreach (TaskObservation observation in result.Observations)
        {
            AppendObservationSummary(builder, observation);
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendObservationSummary(StringBuilder builder, TaskObservation observation)
    {
        if (StringHelper.NormalizeAction(observation.Action) == "shell-script")
        {
            builder.AppendLine($"- Step {observation.Step} {observation.Action}:");
            foreach (string line in TrimObservationResult(observation.Result)
                         .Replace("\r\n", "\n", StringComparison.Ordinal)
                         .Split('\n'))
            {
                builder.AppendLine($"  {line}");
            }

            return;
        }

        builder.AppendLine($"- Step {observation.Step} {observation.Action}: {StringHelper.FirstLine(observation.Result)}");
    }

    private static string TrimObservationResult(string result)
    {
        const int maxCharacters = 4000;
        string trimmed = result.TrimEnd();
        return trimmed.Length <= maxCharacters
            ? trimmed
            : trimmed[..maxCharacters] + "\n...(truncated)";
    }

    private static string BuildFailureSummary(ExecutionResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Execution stopped.");
        builder.AppendLine(result.ErrorMessage ?? "A step failed.");
        builder.AppendLine("Adaptive replanning was attempted when the failure was recoverable and budget remained.");
        return builder.ToString().TrimEnd();
    }

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
                    AgentTaskBase.CreateChatOptions(0.7));
            }

            PotatoConsole.WriteAgentResponse(greeting.Text);
        }
        catch (Exception ex)
        {
            PotatoConsole.WriteStatus($"Skipping startup greeting: {ex.Message}");
        }
    }

    private void SwitchModel(IChatClient selectedOpenAiClient)
    {
        currentOpenAiClient = selectedOpenAiClient;
        chatClientState.SetOpenAiClient(selectedOpenAiClient);
    }

    private string GetExecutionMode() =>
        executionMode == ExecutionMode.ReAct ? "react" : "pipeline";

    private void SetExecutionMode(string mode)
    {
        executionMode = ParseExecutionMode(mode);
        appSettingsStore.SetExecutionMode(GetExecutionMode());
        ResetConversationState();
    }

    private static ExecutionMode ParseExecutionMode(string? mode)
    {
        string normalized = mode?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is "pipeline" or "plan" or "deterministic"
            ? ExecutionMode.Pipeline
            : ExecutionMode.ReAct;
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

    private enum ExecutionMode
    {
        Pipeline,
        ReAct
    }
}
