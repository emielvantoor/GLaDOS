using GLaDOS.Core.Models;

namespace GLaDOS.Core.Protocols;

public class ClaudeProtocol : QwenProtocol
{
    public override string Name => "Claude";

    public override string BuildToolResponse(AgentToolCall toolCall, string toolResult)
    {
        return $"<tool_result name=\"{toolCall.ToolName}\">\n{toolResult}\n</tool_result>\nUse this result to continue.";
    }
}
