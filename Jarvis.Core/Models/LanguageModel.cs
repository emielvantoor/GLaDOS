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
    
    // Transport-only generation: protocols own prompt construction and response parsing.
    public Task<string> GenerateResponseAsync(
        string prompt,
        ChatOptions options,
        CancellationToken cancellationToken = default)
    {
        return OnGenerateResponseAsync(prompt, options, cancellationToken);
    }
    
    protected abstract Task<string> OnGenerateResponseAsync(
        string prompt,
        ChatOptions options,
        CancellationToken cancellationToken = default);
}
