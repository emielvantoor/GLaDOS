namespace Jarvis.Core.Interfaces;

public record AgentMessage(AgentRole Role, string Content, string? ToolCallName = null, string? ToolCallArgs = null);

public class ChatOptions
{
    public float? Temperature { get; set; }
}