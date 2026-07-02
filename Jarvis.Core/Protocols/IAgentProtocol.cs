using Jarvis.Core.Interfaces;
using Jarvis.Core.Models;

namespace Jarvis.Core.Protocols;

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

    bool SupportsThinking { get; }
}
