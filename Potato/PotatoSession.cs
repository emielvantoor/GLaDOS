using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

internal sealed class PotatoSession
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly Uri gladosEndpoint;
    private readonly GladosChatClientFactory clientFactory;
    private readonly ModelSelector modelSelector;
    private readonly ExecutionMemory executionMemory = new();
    private readonly AgentTools agentTools;
    private readonly FileMentionExpander fileMentionExpander = new();
    private readonly PotatoRuntimeOptions options;
    private readonly PotatoAppSettingsStore appSettingsStore;
    private readonly List<string> inputHistory = [];
    private readonly List<SessionTranscript> archivedSessions = [];
    private readonly List<ChatMessage> chatHistory = [];
    private readonly object taskCancellationLock = new();

    private IChatClient currentOpenAiClient;
    private IChatClient currentClient;
    private int nextSessionNumber = 1;
    private int currentSessionNumber;
    private string? currentSessionSubject;
    private DateTime currentSessionStartedAt;
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
        agentTools = new AgentTools(executionMemory, () => currentOpenAiClient, options);
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
            () => currentClient,
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
        EnsureCurrentSession(userInput);
        chatHistory.Add(new ChatMessage(ChatRole.User, expandedGoal));

        try
        {
            List<AgentTask> tasks = await PlanAsync(expandedGoal, cancellationToken);
            chatHistory.Add(new ChatMessage(ChatRole.Assistant, FormatTaskList(tasks)));
            PotatoConsole.WriteAgentResponse(FormatTaskList(tasks));

            ExecutionResult result = await ExecutePlanAsync(expandedGoal, tasks, cancellationToken);
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

    private async Task<List<AgentTask>> PlanAsync(string goal, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, PromptLibrary.PlannerSystemPrompt),
            new(ChatRole.User, $"Working directory: {Environment.CurrentDirectory}\n\nUser goal:\n{goal}")
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress("Planning deterministic task list..."))
        {
            response = await currentOpenAiClient.GetResponseAsync(messages, CreateChatOptions(0.0), cancellationToken);
        }

        string json = ExtractJsonArray(response.Text);
        List<AgentTask>? tasks = JsonSerializer.Deserialize<List<AgentTask>>(json, JsonOptions);
        if (tasks is null || tasks.Count == 0)
        {
            throw new InvalidOperationException("Planner returned no tasks.");
        }

        ValidateTasks(tasks);
        return tasks.OrderBy(task => task.Step).ToList();
    }

    private async Task<ExecutionResult> ExecutePlanAsync(
        string goal,
        IReadOnlyList<AgentTask> tasks,
        CancellationToken cancellationToken)
    {
        var observations = new List<TaskObservation>();
        var context = new ExecutorContext();
        executionMemory.Clear();

        foreach (AgentTask task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PotatoConsole.WriteStatus($"Executing step {task.Step}: {task.Action} {task.Argument}");

            try
            {
                string result = await ExecuteTaskAsync(goal, task, context, observations, cancellationToken);
                observations.Add(new TaskObservation(task.Step, task.Action, task.Argument, result));

                if (IsFailureResult(result))
                {
                    return ExecutionResult.Failed(observations, $"Step {task.Step} failed: {FirstLine(result)}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                observations.Add(new TaskObservation(task.Step, task.Action, task.Argument, $"Error: {ex.Message}"));
                return ExecutionResult.Failed(observations, $"Step {task.Step} threw an exception: {ex.Message}");
            }
        }

        return ExecutionResult.Succeeded(observations);
    }

    private async Task<string> ExecuteTaskAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        CancellationToken cancellationToken)
    {
        string action = NormalizeAction(task.Action);
        return action switch
        {
            "read" => ReadFile(task.Argument, context),
            "list" => agentTools.ListFiles(task.Argument, recursive: false),
            "list-recursive" => agentTools.ListFiles(task.Argument, recursive: true),
            "inspect-project" => InspectProject(task.Argument),
            "search-files" => agentTools.SearchFiles(task.Argument),
            "search" or "search-contents" => agentTools.SearchFileContents(task.Argument),
            "summarize" => await SummarizePathAsync(task.Argument),
            "review-code" => await ExecuteCodeReviewTaskAsync(goal, task, context, observations, cancellationToken),
            "patch" => await ExecutePatchTaskAsync(goal, task, context, observations, cancellationToken),
            "create" => await ExecuteCreateTaskAsync(goal, task, context, observations, cancellationToken),
            "write-summary" or "write-documentation" or "explain-to-user" =>
                await ExecuteTextGenerationTaskAsync(goal, task, context, observations, cancellationToken),
            "shell" or "verify" => await agentTools.ExecuteShellCommandAsync(task.Argument),
            _ => $"Error: Unsupported planner action '{task.Action}'. Supported actions: read, list, list-recursive, inspect_project, search-files, search, summarize, review_code, patch, create, write_summary, write_documentation, explain_to_user, shell, verify."
        };
    }

    private string ReadFile(string filePath, ExecutorContext context)
    {
        string result = agentTools.ReadFileContent(filePath);
        if (!IsFailureResult(result))
        {
            context.LastReadFilePath = filePath;
            context.LastReadFileContent = result;
        }

        return result;
    }

    private async Task<string> SummarizePathAsync(string path)
    {
        string? resolvedPath = ResolveLocalPath(path);
        if (resolvedPath is not null && Directory.Exists(resolvedPath))
        {
            return InspectDirectory(resolvedPath);
        }

        return await agentTools.SummarizeFilePurpose(path);
    }

    private string InspectProject(string directoryPath)
    {
        string root = ResolveExistingDirectory(directoryPath) ?? Environment.CurrentDirectory;
        var builder = new StringBuilder();
        builder.AppendLine($"Project inspection: {root}");
        builder.AppendLine();
        builder.AppendLine("Top-level files and folders:");
        builder.AppendLine(agentTools.ListFiles(root, recursive: false, maxEntries: 300));
        builder.AppendLine();
        builder.AppendLine("Project manifests:");
        builder.AppendLine(agentTools.ListProjectFiles(root));
        builder.AppendLine();
        builder.AppendLine("Likely source, documentation, and test files:");
        builder.AppendLine(agentTools.SearchFiles(
            ".sln|.csproj|.fsproj|.vbproj|package.json|pyproject.toml|Cargo.toml|go.mod|pom.xml|build.gradle|README|.md|.cs|.fs|.ts|.js|test|tests|src|source",
            root,
            recursive: true,
            maxMatches: 300));

        return builder.ToString();
    }

    private string InspectDirectory(string directoryPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Directory inspection: {directoryPath}");
        builder.AppendLine();
        builder.AppendLine(agentTools.ListFiles(directoryPath, recursive: false, maxEntries: 300));
        builder.AppendLine();
        builder.AppendLine("Project manifests under this directory:");
        builder.AppendLine(agentTools.ListProjectFiles(directoryPath));
        return builder.ToString();
    }

    private static string? ResolveExistingDirectory(string? path)
    {
        string? resolvedPath = ResolveLocalPath(path);
        return resolvedPath is not null && Directory.Exists(resolvedPath) ? resolvedPath : null;
    }

    private static string? ResolveLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string trimmed = path.Trim();
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

    private async Task<string> ExecutePatchTaskAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.LastReadFilePath) ||
            string.IsNullOrWhiteSpace(context.LastReadFileContent))
        {
            return "Error: Patch step requires a successful read step immediately before it or earlier in the plan.";
        }

        string codeBlock = SelectTargetCodeBlock(context.LastReadFileContent, task.Argument);
        SearchReplacePatch patch = await GeneratePatchAsync(
            goal,
            task,
            context.LastReadFilePath,
            codeBlock,
            observations,
            cancellationToken);

        if (!string.Equals(patch.FilePath, context.LastReadFilePath, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(patch.FilePath))
        {
            return $"Error: Patch model targeted '{patch.FilePath}', but the executor only allows patching the last read file '{context.LastReadFilePath}'.";
        }

        return await agentTools.ApplySearchReplaceAsync(context.LastReadFilePath, patch.Search, patch.Replace);
    }

    private async Task<string> ExecuteCodeReviewTaskAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.LastReadFilePath) ||
            string.IsNullOrWhiteSpace(context.LastReadFileContent))
        {
            return "Error: review_code requires a successful read step first.";
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, PromptLibrary.CodeReviewSystemPrompt),
            new(
                ChatRole.User,
                $"Goal:\n{goal}\n\n" +
                $"Review task:\n{task.Argument}\n\n" +
                $"File path:\n{context.LastReadFilePath}\n\n" +
                "Prior observations:\n" +
                FormatObservations(observations) +
                "\n\nFile contents:\n```csharp\n" +
                context.LastReadFileContent +
                "\n```")
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress($"Reviewing {PathResolver.FormatPathForDisplay(context.LastReadFilePath)}..."))
        {
            response = await currentOpenAiClient.GetResponseAsync(
                messages,
                CreateChatOptions(task.GetTargetTemperature()),
                cancellationToken);
        }

        return string.IsNullOrWhiteSpace(response.Text)
            ? "Error: Code review returned an empty response."
            : response.Text.Trim();
    }

    private async Task<string> ExecuteCreateTaskAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        CancellationToken cancellationToken)
    {
        CreatedFile createdFile = await GenerateNewFileAsync(goal, task, context, observations, cancellationToken);
        return await agentTools.CreateFileAsync(createdFile.FilePath, createdFile.Content);
    }

    private async Task<string> ExecuteTextGenerationTaskAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, PromptLibrary.UserTextSystemPrompt),
            new(
                ChatRole.User,
                $"Action: {task.Action}\n" +
                $"Temperature: {task.GetTargetTemperature():0.0}\n\n" +
                $"Goal:\n{goal}\n\n" +
                $"Task:\n{task.Argument}\n\n" +
                $"Last read file: {context.LastReadFilePath ?? "(none)"}\n\n" +
                "Prior observations:\n" +
                FormatObservations(observations))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress($"Generating {task.Action} response..."))
        {
            response = await currentOpenAiClient.GetResponseAsync(
                messages,
                CreateChatOptions(task.GetTargetTemperature()),
                cancellationToken);
        }

        return string.IsNullOrWhiteSpace(response.Text)
            ? "Error: Text generation returned an empty response."
            : response.Text.Trim();
    }

    private async Task<SearchReplacePatch> GeneratePatchAsync(
        string goal,
        AgentTask task,
        string filePath,
        string codeBlock,
        IReadOnlyList<TaskObservation> observations,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, PromptLibrary.PatchSystemPrompt),
            new(
                ChatRole.User,
                "Return a JSON object only.\n\n" +
                $"Goal:\n{goal}\n\n" +
                $"Patch task:\n{task.Argument}\n\n" +
                $"Target file:\n{filePath}\n\n" +
                "Prior observations:\n" +
                FormatObservations(observations) +
                "\n\nSpecific code block to edit:\n```text\n" +
                codeBlock +
                "\n```")
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress($"Generating targeted patch for {PathResolver.FormatPathForDisplay(filePath)}..."))
        {
            response = await currentOpenAiClient.GetResponseAsync(
                messages,
                CreateChatOptions(task.GetTargetTemperature()),
                cancellationToken);
        }

        string json = ExtractJsonObject(response.Text);
        SearchReplacePatch? patch = JsonSerializer.Deserialize<SearchReplacePatch>(json, JsonOptions);
        if (patch is null ||
            string.IsNullOrEmpty(patch.Search) ||
            patch.Replace is null)
        {
            throw new InvalidOperationException("Patch model did not return valid search/replace JSON.");
        }

        if (!codeBlock.Contains(patch.Search, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Patch model returned SEARCH text that is not present in the targeted code block.");
        }

        return patch with { FilePath = string.IsNullOrWhiteSpace(patch.FilePath) ? filePath : patch.FilePath };
    }

    private async Task<CreatedFile> GenerateNewFileAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, PromptLibrary.CreateFileSystemPrompt),
            new(
                ChatRole.User,
                "Return a JSON object only.\n\n" +
                $"Goal:\n{goal}\n\n" +
                $"Create task:\n{task.Argument}\n\n" +
                $"Last read file: {context.LastReadFilePath ?? "(none)"}\n\n" +
                "Prior observations:\n" +
                FormatObservations(observations))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress("Generating new file content..."))
        {
            response = await currentOpenAiClient.GetResponseAsync(
                messages,
                CreateChatOptions(task.GetTargetTemperature()),
                cancellationToken);
        }

        string json = ExtractJsonObject(response.Text);
        CreatedFile? createdFile = JsonSerializer.Deserialize<CreatedFile>(json, JsonOptions);
        if (createdFile is null ||
            string.IsNullOrWhiteSpace(createdFile.FilePath) ||
            createdFile.Content is null)
        {
            throw new InvalidOperationException("Create-file model did not return valid JSON.");
        }

        return createdFile;
    }

    private static ChatOptions CreateChatOptions(double temperature) =>
        new()
        {
            Temperature = (float)temperature
        };

    private static void ValidateTasks(IEnumerable<AgentTask> tasks)
    {
        int expectedStep = 1;
        foreach (AgentTask task in tasks.OrderBy(task => task.Step))
        {
            if (task.Step != expectedStep)
            {
                throw new InvalidOperationException($"Planner step numbers must be sequential. Expected {expectedStep}, got {task.Step}.");
            }

            if (string.IsNullOrWhiteSpace(task.Action))
            {
                throw new InvalidOperationException($"Planner step {task.Step} has no action.");
            }

            if (string.IsNullOrWhiteSpace(task.Argument))
            {
                throw new InvalidOperationException($"Planner step {task.Step} has no argument.");
            }

            expectedStep++;
        }
    }

    private static string ExtractJsonArray(string text)
    {
        string trimmed = StripCodeFence(text).Trim();
        int start = trimmed.IndexOf('[', StringComparison.Ordinal);
        int end = trimmed.LastIndexOf(']');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("Planner did not return a JSON array.");
        }

        return trimmed[start..(end + 1)];
    }

    private static string ExtractJsonObject(string text)
    {
        string trimmed = StripCodeFence(text).Trim();
        int start = trimmed.IndexOf('{', StringComparison.Ordinal);
        int end = trimmed.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("Model did not return a JSON object.");
        }

        return trimmed[start..(end + 1)];
    }

    private static string StripCodeFence(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int firstLineBreak = trimmed.IndexOf('\n', StringComparison.Ordinal);
        int lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstLineBreak < 0 || lastFence <= firstLineBreak)
        {
            return trimmed;
        }

        return trimmed[(firstLineBreak + 1)..lastFence];
    }

    private static string SelectTargetCodeBlock(string fileContent, string patchArgument)
    {
        string[] lines = fileContent.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string[] candidates = ExtractIdentifierCandidates(patchArgument);

        foreach (string candidate in candidates)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], $@"\b{Regex.Escape(candidate)}\b"))
                {
                    continue;
                }

                return SliceLines(lines, Math.Max(0, i - 25), Math.Min(lines.Length, i + 120));
            }
        }

        const int maxPatchContextCharacters = 18_000;
        if (fileContent.Length <= maxPatchContextCharacters)
        {
            return fileContent;
        }

        return fileContent[..maxPatchContextCharacters];
    }

    private static string[] ExtractIdentifierCandidates(string text) =>
        Regex.Matches(text, @"[A-Za-z_][A-Za-z0-9_]{2,}")
            .Select(match => match.Value)
            .Where(value => !CommonPlannerWords.Contains(value, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static readonly string[] CommonPlannerWords =
    [
        "the", "and", "for", "with", "from", "into", "replace", "update", "modify", "refactor",
        "method", "class", "file", "code", "patch", "change", "implementation", "logic"
    ];

    private static string SliceLines(string[] lines, int start, int end)
    {
        var builder = new StringBuilder();
        for (int i = start; i < end; i++)
        {
            builder.AppendLine(lines[i]);
        }

        return builder.ToString();
    }

    private static string NormalizeAction(string action) =>
        action.Trim().ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal);

    private static bool IsFailureResult(string result)
    {
        string firstLine = FirstLine(result).TrimStart();
        return firstLine.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
               firstLine.StartsWith("Rejected ", StringComparison.OrdinalIgnoreCase) ||
               firstLine.EndsWith(" denied", StringComparison.OrdinalIgnoreCase) ||
               firstLine.EndsWith(" failed.", StringComparison.OrdinalIgnoreCase) ||
               firstLine.Contains(" timed out", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLine(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[0];

    private static string FormatTaskList(IReadOnlyList<AgentTask> tasks)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Planner produced this deterministic task list:");
        foreach (AgentTask task in tasks)
        {
            builder.AppendLine($"{task.Step}. {task.Action}: {task.Argument}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatObservations(IEnumerable<TaskObservation> observations)
    {
        var builder = new StringBuilder();
        foreach (TaskObservation observation in observations)
        {
            builder.AppendLine($"Step {observation.Step} {observation.Action} {observation.Argument}:");
            builder.AppendLine(Truncate(observation.Result, 4_000));
            builder.AppendLine();
        }

        return builder.Length == 0 ? "(none)" : builder.ToString();
    }

    private static string BuildSuccessSummary(ExecutionResult result)
    {
        TaskObservation? userFacingObservation = result.Observations.LastOrDefault(observation =>
            NormalizeAction(observation.Action) is "review-code" or "write-summary" or "write-documentation" or "explain-to-user");
        if (userFacingObservation is not null)
        {
            return userFacingObservation.Result;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Execution completed.");
        foreach (TaskObservation observation in result.Observations)
        {
            builder.AppendLine($"- Step {observation.Step} {observation.Action}: {FirstLine(observation.Result)}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildFailureSummary(ExecutionResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Execution stopped.");
        builder.AppendLine(result.ErrorMessage ?? "A step failed.");
        builder.AppendLine("No autonomous recovery loop was started.");
        return builder.ToString().TrimEnd();
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";

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
                greeting = await currentOpenAiClient.GetResponseAsync(greetingMessages, CreateChatOptions(0.7));
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
            PotatoConsole.WriteStatus($"{session.Number}: {session.Subject} ({session.StartedAt:g}, {session.Messages.Count} messages)");
        }

        if (currentSessionNumber != 0)
        {
            PotatoConsole.WriteStatus($"{currentSessionNumber}: {currentSessionSubject} ({currentSessionStartedAt:g}, {chatHistory.Count} messages, current)");
        }
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

        SessionTranscript? archivedSession = archivedSessions.FirstOrDefault(candidate => candidate.Number == sessionNumber);
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
        PromptLibrary.SetUseCompiledDefaultsOnly(useCompiledDefaultsOnly);
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

    private sealed class ExecutorContext
    {
        public string? LastReadFilePath { get; set; }
        public string? LastReadFileContent { get; set; }
    }

    private sealed record TaskObservation(int Step, string Action, string Argument, string Result);

    private sealed record ExecutionResult(
        bool Success,
        IReadOnlyList<TaskObservation> Observations,
        string? ErrorMessage)
    {
        public static ExecutionResult Succeeded(IReadOnlyList<TaskObservation> observations) =>
            new(true, observations, null);

        public static ExecutionResult Failed(IReadOnlyList<TaskObservation> observations, string errorMessage) =>
            new(false, observations, errorMessage);
    }

    private sealed record SessionTranscript(
        int Number,
        string Subject,
        DateTime StartedAt,
        IReadOnlyList<ChatMessage> Messages);
}
