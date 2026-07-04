namespace GLaDOS.Core.Interfaces;

public record AgentResponse(string Text, bool IsToolCall, string? ToolName = null, string? ToolArgs = null);