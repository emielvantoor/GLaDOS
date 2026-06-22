using Jarvis.Core.Interfaces;

namespace Jarvis.Core.Models;

public abstract class LanguageModel
{
    public abstract LanguageModelMetaData ModelMetaData { get; }
    
    public async Task InitializeAsync()
    {
        await OnInitializeAsync();
    }
    
    protected abstract Task OnInitializeAsync();
    
    public IAsyncEnumerable<(string Text, int Percent)> GenerateResponseAsync(string formattedPrompt, CancellationToken cancellationToken = default)
    {
        return OnGenerateResponseAsync(formattedPrompt, cancellationToken);
    }
    
    protected abstract IAsyncEnumerable<(string Text, int Percent)> OnGenerateResponseAsync(string formattedPrompt, CancellationToken cancellationToken = default);
    
    
// De streaming methode die de Agent gebruikt om rollen, geschiedenis en tools te verwerken
    public IAsyncEnumerable<ChatResponseChunk> GenerateChatResponseAsync(
        List<AgentMessage> history, 
        List<AgentToolDefinition> tools, 
        CancellationToken cancellationToken = default)
    {
        return OnGenerateChatResponseAsync(history, tools, cancellationToken);
    }
    
    protected abstract IAsyncEnumerable<ChatResponseChunk> OnGenerateChatResponseAsync(
        List<AgentMessage> history, 
        List<AgentToolDefinition> tools, 
        CancellationToken cancellationToken = default);
}

// Het gestructureerde chunk-type dat door de stream heen vloeit
public record ChatResponseChunk(
    string Text, 
    bool IsToolCall, 
    string? ToolName = null, 
    string? ToolArgs = null
);