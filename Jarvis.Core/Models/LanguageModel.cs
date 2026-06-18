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
}