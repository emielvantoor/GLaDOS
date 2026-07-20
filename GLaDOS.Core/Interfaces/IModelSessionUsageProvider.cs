namespace GLaDOS.Core.Interfaces;

public interface IModelSessionUsageProvider
{
    ModelSessionUsage? GetSessionUsage(string sessionId);
    bool TouchSession(string sessionId);
    bool ReleaseSession(string sessionId);
    int ReleaseInactiveSessions();
}

public sealed record ModelSessionUsage(
    string SessionId,
    int EstimatedTokens,
    int ContextSize,
    DateTimeOffset LastActivityAt);
