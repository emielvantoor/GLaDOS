namespace GLaDOS.Models;

public sealed record PotatoSessionSummary(
    string Id,
    string WorkingDirectory,
    string DisplayName,
    string Model,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    int MessageCount);

public sealed record PotatoSessionDetail(
    string Id,
    string WorkingDirectory,
    string DisplayName,
    string Model,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    IReadOnlyList<PotatoSessionEvent> Events);

public sealed record PotatoSessionEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Kind,
    string Role,
    string Content,
    bool Collapsed);

public sealed record PotatoSessionStartRequest(
    string WorkingDirectory,
    string Model,
    string? DisplayName);

public sealed record PotatoSessionEventRequest(
    string WorkingDirectory,
    string Kind,
    string Role,
    string Content,
    bool Collapsed);

public sealed record PotatoSessionInputRequest(string Content);
