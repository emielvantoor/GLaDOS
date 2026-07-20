using GLaDOS.Models;

namespace GLaDOS.Services;

public sealed class PotatoSessionStore
{
    private static readonly TimeSpan StaleActiveSessionAge = TimeSpan.FromMinutes(5);
    private static readonly string[] SlashCommands =
    [
        "/model",
        "/cd",
        "/ask",
        "/prompts",
        "/webui-input",
        "/sessions",
        "/continue",
        "/transcript",
        "/abort"
    ];
    private readonly object sync = new();
    private readonly Dictionary<string, StoredPotatoSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private long nextSequence;

    public PotatoSessionSummary StartSession(PotatoSessionStartRequest request)
    {
        string workingDirectory = NormalizeWorkingDirectory(request.WorkingDirectory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string id = CreateSessionId(workingDirectory);

        lock (sync)
        {
            if (!sessions.TryGetValue(id, out StoredPotatoSession? session))
            {
                session = new StoredPotatoSession(id, workingDirectory, BuildDisplayName(request, workingDirectory), request.Model, now);
                sessions[id] = session;
            }

            session.Model = request.Model;
            session.DisplayName = BuildDisplayName(request, workingDirectory);
            session.WebUiInputEnabled = IsInputEnabledMode(request.Mode);
            session.Status = "active";
            session.IsProcessing = false;
            session.CurrentProgress = null;
            session.CurrentInputPrompt = null;
            session.ContextUsage = null;
            session.StartedAt = now;
            session.LastActivityAt = now;
            session.Events.Clear();
            AddEvent(session, now, "lifecycle", "status", $"Potato session started in {workingDirectory}", collapsed: true);
            return ToSummary(session);
        }
    }

    public PotatoSessionSummary AddEvent(PotatoSessionEventRequest request)
    {
        string workingDirectory = NormalizeWorkingDirectory(request.WorkingDirectory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string id = CreateSessionId(workingDirectory);

        lock (sync)
        {
            if (!sessions.TryGetValue(id, out StoredPotatoSession? session))
            {
                session = new StoredPotatoSession(id, workingDirectory, Path.GetFileName(workingDirectory), string.Empty, now);
                sessions[id] = session;
            }

            UpdateWorkingDirectory(session, request.CurrentWorkingDirectory);
            session.Status = request.Kind.Equals("stopped", StringComparison.OrdinalIgnoreCase) ? "stopped" : "active";
            session.LastActivityAt = now;
            if (TryApplySessionStateEvent(session, request.Kind, request.Content, request.ContextUsage))
            {
                return ToSummary(session);
            }

            AddEvent(session, now, request.Kind, request.Role, request.Content, request.Collapsed);
            return ToSummary(session);
        }
    }

    public IReadOnlyList<PotatoSessionSummary> GetActiveSessions()
    {
        DateTimeOffset staleCutoff = DateTimeOffset.UtcNow - StaleActiveSessionAge;

        lock (sync)
        {
            foreach (StoredPotatoSession session in sessions.Values)
            {
                if (session.Status == "active" && session.LastActivityAt < staleCutoff)
                {
                    session.Status = "stale";
                }
            }

            return sessions.Values
                .Where(session => session.Status == "active")
                .OrderByDescending(session => session.LastActivityAt)
                .Select(ToSummary)
                .ToArray();
        }
    }

    public PotatoSessionDetail? GetSession(string id)
    {
        lock (sync)
        {
            return sessions.TryGetValue(id, out StoredPotatoSession? session) ? ToDetail(session) : null;
        }
    }

    public bool EnqueueInput(string id, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        string normalizedContent = content.TrimEnd();

        lock (sync)
        {
            if (!sessions.TryGetValue(id, out StoredPotatoSession? session))
            {
                return false;
            }

            if (!session.WebUiInputEnabled)
            {
                return false;
            }

            session.PendingInputs.Enqueue(normalizedContent);
            session.LastActivityAt = DateTimeOffset.UtcNow;
            AddEvent(session, session.LastActivityAt, "input", "user", normalizedContent, collapsed: true);
            return true;
        }
    }

    public bool EnqueuePermissionChoice(string id, string choice)
    {
        string normalizedChoice = choice.Trim().ToLowerInvariant();
        if (normalizedChoice is not ("once" or "always" or "deny"))
        {
            return false;
        }

        lock (sync)
        {
            if (!sessions.TryGetValue(id, out StoredPotatoSession? session))
            {
                return false;
            }

            // Permissions are deliberately available even when the Web UI is
            // observe-only. This does not open arbitrary prompt input.
            session.PendingInputs.Enqueue(normalizedChoice);
            session.LastActivityAt = DateTimeOffset.UtcNow;
            AddEvent(session, session.LastActivityAt, "input", "user", normalizedChoice, collapsed: true);
            return true;
        }
    }

    public string? DequeueInput(string workingDirectory)
    {
        string normalizedWorkingDirectory = NormalizeWorkingDirectory(workingDirectory);
        string id = CreateSessionId(normalizedWorkingDirectory);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (sync)
        {
            if (!sessions.TryGetValue(id, out StoredPotatoSession? session))
            {
                session = new StoredPotatoSession(
                    id,
                    normalizedWorkingDirectory,
                    Path.GetFileName(normalizedWorkingDirectory),
                    string.Empty,
                    now);
                sessions[id] = session;
            }

            session.Status = "active";
            session.LastActivityAt = now;
            return session.PendingInputs.TryDequeue(out string? input) ? input : null;
        }
    }

    public IReadOnlyList<PotatoSessionCompletion>? GetCompletions(string id, string content, int cursorIndex)
    {
        string workingDirectory;
        lock (sync)
        {
            if (!sessions.TryGetValue(id, out StoredPotatoSession? session))
            {
                return null;
            }

            workingDirectory = session.WorkingDirectory;
        }

        cursorIndex = Math.Clamp(cursorIndex, 0, content.Length);
        string text = content[..cursorIndex];
        if (cursorIndex != content.Length)
        {
            return [];
        }

        if (TryGetSlashCommandCompletions(text, out List<PotatoSessionCompletion> slashCompletions))
        {
            return slashCompletions;
        }

        if (TryGetCdArgument(text, out int argumentStartIndex, out string argument))
        {
            List<PathCompletion> pathCompletions = FindPathCompletions(
                workingDirectory,
                argument,
                includeFiles: false,
                appendDirectorySeparator: false);

            bool completeBareCommand = argumentStartIndex == text.Length &&
                                       text.Equals("/cd", StringComparison.OrdinalIgnoreCase);
            return pathCompletions
                .Select(value => completeBareCommand
                    ? new PotatoSessionCompletion(" " + value.ReplacementText, cursorIndex, " " + value.DisplayText, "directory")
                    : new PotatoSessionCompletion(value.ReplacementText, argumentStartIndex, value.DisplayText, "directory"))
                .Where(value => value.DisplayText.Length > 0 || value.ReplacementText.Length > 0)
                .Take(12)
                .ToArray();
        }

        if (TryGetFileMentionArgument(text, out int mentionArgumentStartIndex, out string mentionArgument))
        {
            return FindPathCompletions(
                    workingDirectory,
                    mentionArgument,
                    includeFiles: true,
                    appendDirectorySeparator: true)
                .Select(value => new PotatoSessionCompletion(value.ReplacementText, mentionArgumentStartIndex, value.DisplayText, "path"))
                .Where(value => value.DisplayText.Length > 0 || value.ReplacementText.Length > 0)
                .Take(12)
                .ToArray();
        }

        return [];
    }

    private void AddEvent(StoredPotatoSession session, DateTimeOffset timestamp, string kind, string role, string content, bool collapsed)
    {
        session.Events.Add(new PotatoSessionEvent(
            Interlocked.Increment(ref nextSequence),
            timestamp,
            string.IsNullOrWhiteSpace(kind) ? "message" : kind,
            string.IsNullOrWhiteSpace(role) ? "status" : role,
            content,
            collapsed));
    }

    private static void UpdateWorkingDirectory(StoredPotatoSession session, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return;
        }

        string normalizedWorkingDirectory = NormalizeWorkingDirectory(workingDirectory);
        if (string.Equals(session.WorkingDirectory, normalizedWorkingDirectory, StringComparison.Ordinal))
        {
            return;
        }

        string oldDisplayName = Path.GetFileName(session.WorkingDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        bool hasAutomaticDisplayName = string.IsNullOrWhiteSpace(session.DisplayName) ||
                                       string.Equals(session.DisplayName, oldDisplayName, StringComparison.Ordinal);

        session.WorkingDirectory = normalizedWorkingDirectory;
        if (!hasAutomaticDisplayName)
        {
            return;
        }

        session.DisplayName = Path.GetFileName(normalizedWorkingDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
    }

    private static PotatoSessionSummary ToSummary(StoredPotatoSession session) =>
        new(
            session.Id,
            session.WorkingDirectory,
            session.DisplayName,
            session.Model,
            session.Status,
            session.IsProcessing,
            session.CurrentProgress,
            session.CurrentInputPrompt,
            session.ContextUsage,
            session.WebUiInputEnabled,
            session.StartedAt,
            session.LastActivityAt,
            session.Events.Count);

    private static PotatoSessionDetail ToDetail(StoredPotatoSession session) =>
        new(
            session.Id,
            session.WorkingDirectory,
            session.DisplayName,
            session.Model,
            session.Status,
            session.IsProcessing,
            session.CurrentProgress,
            session.CurrentInputPrompt,
            session.ContextUsage,
            session.WebUiInputEnabled,
            session.StartedAt,
            session.LastActivityAt,
            session.Events.ToArray());

    private static bool TryApplySessionStateEvent(
        StoredPotatoSession session,
        string kind,
        string content,
        PotatoContextUsage? contextUsage)
    {
        if (kind.Equals("progress-start", StringComparison.OrdinalIgnoreCase) ||
            kind.Equals("progress-update", StringComparison.OrdinalIgnoreCase))
        {
            session.IsProcessing = true;
            session.CurrentProgress = string.IsNullOrWhiteSpace(content) ? "Potato is thinking" : content.Trim();
            return true;
        }

        if (kind.Equals("progress-end", StringComparison.OrdinalIgnoreCase))
        {
            session.IsProcessing = false;
            session.CurrentProgress = null;
            return true;
        }

        if (kind.Equals("input-prompt", StringComparison.OrdinalIgnoreCase))
        {
            session.CurrentInputPrompt = string.IsNullOrWhiteSpace(content)
                ? null
                : content.Trim();
            return true;
        }

        if (kind.Equals("input-prompt-clear", StringComparison.OrdinalIgnoreCase))
        {
            session.CurrentInputPrompt = null;
            return true;
        }

        if (kind.Equals("webui-input-enabled", StringComparison.OrdinalIgnoreCase))
        {
            session.WebUiInputEnabled = true;
            return false;
        }

        if (kind.Equals("webui-input-disabled", StringComparison.OrdinalIgnoreCase))
        {
            session.WebUiInputEnabled = false;
            session.PendingInputs.Clear();
            return false;
        }

        if (kind.Equals("context-usage", StringComparison.OrdinalIgnoreCase))
        {
            session.ContextUsage = contextUsage ?? new PotatoContextUsage(
                PromptTokens: 0,
                ContextSize: 0,
                Percentage: 0,
                MaxOutputTokens: 0,
                HeadroomAfterReservedOutput: 0,
                ExceedsContext: false,
                Summary: content.Trim());
            return true;
        }

        return false;
    }

    private static bool IsInputEnabledMode(string? mode) =>
        string.Equals(mode, "input-enabled", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWorkingDirectory(string workingDirectory) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory);

    private static string CreateSessionId(string workingDirectory) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(workingDirectory)));

    private static string BuildDisplayName(PotatoSessionStartRequest request, string workingDirectory) =>
        string.IsNullOrWhiteSpace(request.DisplayName)
            ? Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : request.DisplayName.Trim();

    private static bool TryGetSlashCommandCompletions(string text, out List<PotatoSessionCompletion> completions)
    {
        completions = [];
        if (!text.StartsWith("/", StringComparison.Ordinal) || text.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        completions = SlashCommands
            .Where(command => command.StartsWith(text, StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(command, text, StringComparison.OrdinalIgnoreCase))
            .Select(command => new PotatoSessionCompletion(command + " ", 0, command[text.Length..] + " ", "command"))
            .Take(12)
            .ToList();
        return completions.Count > 0;
    }

    private static bool TryGetCdArgument(string text, out int argumentStartIndex, out string argument)
    {
        argumentStartIndex = 0;
        argument = string.Empty;
        if (!text.StartsWith("/cd", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text.Length == 3)
        {
            argumentStartIndex = text.Length;
            return true;
        }

        if (!char.IsWhiteSpace(text[3]))
        {
            return false;
        }

        argumentStartIndex = 4;
        while (argumentStartIndex < text.Length && char.IsWhiteSpace(text[argumentStartIndex]))
        {
            argumentStartIndex++;
        }

        argument = text[argumentStartIndex..].Trim('"', '\'');
        return true;
    }

    private static bool TryGetFileMentionArgument(string text, out int argumentStartIndex, out string argument)
    {
        argumentStartIndex = 0;
        argument = string.Empty;
        int mentionStart = text.LastIndexOf('@');
        if (mentionStart < 0 ||
            (mentionStart > 0 && !char.IsWhiteSpace(text[mentionStart - 1])))
        {
            return false;
        }

        argumentStartIndex = mentionStart + 1;
        argument = text[(mentionStart + 1)..].Trim('"', '\'');
        return true;
    }

    private static List<PathCompletion> FindPathCompletions(
        string workingDirectory,
        string argument,
        bool includeFiles,
        bool appendDirectorySeparator)
    {
        string normalizedArgument = argument.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        int separatorIndex = normalizedArgument.LastIndexOf(Path.DirectorySeparatorChar);
        string baseArgument = separatorIndex >= 0 ? normalizedArgument[..(separatorIndex + 1)] : string.Empty;
        string namePrefix = separatorIndex >= 0 ? normalizedArgument[(separatorIndex + 1)..] : normalizedArgument;

        string baseDirectory;
        try
        {
            baseDirectory = string.IsNullOrWhiteSpace(baseArgument)
                ? workingDirectory
                : ResolveMentionedPath(workingDirectory, baseArgument) ?? workingDirectory;
        }
        catch
        {
            return [];
        }

        if (!Directory.Exists(baseDirectory))
        {
            return [];
        }

        var candidates = new List<PathCompletionCandidate>();
        try
        {
            candidates.AddRange(Directory.EnumerateDirectories(baseDirectory)
                .Select(path => new PathCompletionCandidate(Path.GetFileName(path), IsDirectory: true))
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name)));

            if (includeFiles)
            {
                candidates.AddRange(Directory.EnumerateFiles(baseDirectory)
                    .Select(path => new PathCompletionCandidate(Path.GetFileName(path), IsDirectory: false))
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }

        if (namePrefix.StartsWith(".", StringComparison.Ordinal))
        {
            candidates.Insert(0, new PathCompletionCandidate("..", IsDirectory: true));
        }

        return candidates
            .Where(candidate => candidate.Name is not null &&
                                candidate.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(candidate.Name, namePrefix, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.IsDirectory)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate =>
            {
                string replacementText = baseArgument + candidate.Name;
                if (candidate.IsDirectory && appendDirectorySeparator)
                {
                    replacementText += Path.DirectorySeparatorChar;
                }

                string displayText = replacementText.Length >= normalizedArgument.Length
                    ? replacementText[normalizedArgument.Length..]
                    : string.Empty;
                return new PathCompletion(replacementText, displayText);
            })
            .Where(value => value.DisplayText.Length > 0 ||
                            !string.Equals(value.ReplacementText, normalizedArgument, StringComparison.Ordinal))
            .ToList();
    }

    private static string? ResolveMentionedPath(string workingDirectory, string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        if (Uri.TryCreate(rawPath, UriKind.Absolute, out Uri? uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        string expandedPath = rawPath.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), rawPath[2..])
            : rawPath;
        string workspaceRoot = FindGitRepositoryRoot(workingDirectory) ?? workingDirectory;

        if (Path.IsPathRooted(expandedPath))
        {
            string rootedPath = Path.GetFullPath(expandedPath);
            return ResolveExistingPathWithCurrentCasing(rootedPath) ?? rootedPath;
        }

        string workingDirectoryPath = Path.GetFullPath(Path.Combine(workingDirectory, expandedPath));
        string? resolvedWorkingDirectoryPath = ResolveExistingPathWithCurrentCasing(workingDirectoryPath);
        if (resolvedWorkingDirectoryPath is not null)
        {
            return resolvedWorkingDirectoryPath;
        }

        string workspacePath = Path.GetFullPath(Path.Combine(workspaceRoot, expandedPath));
        return ResolveExistingPathWithCurrentCasing(workspacePath) ?? workspacePath;
    }

    private static string? ResolveExistingPathWithCurrentCasing(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            return path;
        }

        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        string current = root;
        string relative = Path.GetRelativePath(root, fullPath);
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(segment) || segment == ".")
            {
                continue;
            }

            if (!Directory.Exists(current))
            {
                return null;
            }

            string? match = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), segment, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return null;
            }

            current = match;
        }

        return Directory.Exists(current) || File.Exists(current) ? current : null;
    }

    private static string? FindGitRepositoryRoot(string directoryPath)
    {
        var directory = new DirectoryInfo(directoryPath);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private sealed class StoredPotatoSession(
        string id,
        string workingDirectory,
        string displayName,
        string model,
        DateTimeOffset startedAt)
    {
        public string Id { get; } = id;
        public string WorkingDirectory { get; set; } = workingDirectory;
        public string DisplayName { get; set; } = displayName;
        public string Model { get; set; } = model;
        public string Status { get; set; } = "active";
        public bool IsProcessing { get; set; }
        public string? CurrentProgress { get; set; }
        public string? CurrentInputPrompt { get; set; }
        public PotatoContextUsage? ContextUsage { get; set; }
        public bool WebUiInputEnabled { get; set; }
        public DateTimeOffset StartedAt { get; set; } = startedAt;
        public DateTimeOffset LastActivityAt { get; set; } = startedAt;
        public List<PotatoSessionEvent> Events { get; } = [];
        public Queue<string> PendingInputs { get; } = [];
    }

    private sealed record PathCompletion(string ReplacementText, string DisplayText);

    private sealed record PathCompletionCandidate(string? Name, bool IsDirectory);
}
