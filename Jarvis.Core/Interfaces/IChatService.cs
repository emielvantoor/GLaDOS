namespace Jarvis.Core.Interfaces;

public interface IChatService
{
    // De Core Agent stuurt de geschiedenis en de beschikbare tools naar de AI-service
    Task<AgentResponse> GetResponseAsync(List<AgentMessage> history, List<AgentToolDefinition> tools);
}