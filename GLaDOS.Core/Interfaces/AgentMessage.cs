namespace GLaDOS.Core.Interfaces;

public record AgentMessage(AgentRole Role, string Content, string? ToolCallName = null, string? ToolCallArgs = null);

public class ChatOptions
{
    public float? Temperature { get; set; }

    public int? ContextSize { get; set; }
    
    public int? MaxTokenLength { get; set; }

    public IReadOnlyList<string>? StopSequences { get; set; }
}
