using GLaDOS.Core.Interfaces;
using GLaDOS.Core.Models;

namespace GLaDOS.Core.Protocols;

public interface IAgentProtocol
{
    string Name { get; }

    string BuildPrompt(
        List<AgentMessage> history,
        IReadOnlyList<AgentToolDefinition> tools);

    IEnumerable<AgentToolCall> ParseResponse(string response);

    string BuildToolResponse(
        AgentToolCall toolCall,
        string toolResult);

    string CleanResponse(string response);

    bool SupportsThinking { get; }
}
