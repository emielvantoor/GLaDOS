using GLaDOS.Models;

namespace GLaDOS.Services;

public sealed class PotatoSessionStore
{
    private static readonly TimeSpan StaleActiveSessionAge = TimeSpan.FromMinutes(5);
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
}
