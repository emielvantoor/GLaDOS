namespace GLaDOS.Core.Models;

public class AgentToolResult
{
    public required AgentToolCall ToolCall { get; init; }

    public required string Output { get; init; }

    public bool IsExternal { get; init; }
}
