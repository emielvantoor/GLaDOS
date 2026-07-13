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
        "/mode",
        "/prompts",
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
            session.Status = "active";
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

            session.Status = request.Kind.Equals("stopped", StringComparison.OrdinalIgnoreCase) ? "stopped" : "active";
            session.LastActivityAt = now;
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

            session.PendingInputs.Enqueue(normalizedContent);
            session.LastActivityAt = DateTimeOffset.UtcNow;
            AddEvent(session, session.LastActivityAt, "input", "user", normalizedContent, collapsed: true);
            return true;
        }
    }

    public string? DequeueInput(string workingDirectory)
    {
        string normalizedWorkingDirectory = NormalizeWorkingDirectory(workingDirectory);
        string id = CreateSessionId(normalizedWorkingDirectory);

        lock (sync)
        {
            return sessions.TryGetValue(id, out StoredPotatoSession? session) &&
                   session.PendingInputs.TryDequeue(out string? input)
                ? input
                : null;
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

    private static PotatoSessionSummary ToSummary(StoredPotatoSession session) =>
        new(
            session.Id,
            session.WorkingDirectory,
            session.DisplayName,
            session.Model,
            session.Status,
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
            session.StartedAt,
            session.LastActivityAt,
            session.Events.ToArray());

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
        int tokenStart = text.LastIndexOfAny([' ', '\t', '\r', '\n']);
        tokenStart = tokenStart < 0 ? 0 : tokenStart + 1;
        if (tokenStart >= text.Length || text[tokenStart] != '@')
        {
            return false;
        }

        argumentStartIndex = tokenStart + 1;
        argument = text[(tokenStart + 1)..].Trim('"', '\'');
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

        return Path.GetFullPath(Path.IsPathRooted(expandedPath)
            ? expandedPath
            : Path.Combine(workspaceRoot, expandedPath));
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
        public string WorkingDirectory { get; } = workingDirectory;
        public string DisplayName { get; set; } = displayName;
        public string Model { get; set; } = model;
        public string Status { get; set; } = "active";
        public DateTimeOffset StartedAt { get; set; } = startedAt;
        public DateTimeOffset LastActivityAt { get; set; } = startedAt;
        public List<PotatoSessionEvent> Events { get; } = [];
        public Queue<string> PendingInputs { get; } = [];
    }

    private sealed record PathCompletion(string ReplacementText, string DisplayText);

    private sealed record PathCompletionCandidate(string? Name, bool IsDirectory);
}
