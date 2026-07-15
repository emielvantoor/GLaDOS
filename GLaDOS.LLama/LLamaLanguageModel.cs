using System.Text;
using GLaDOS.Core.Interfaces;
using GLaDOS.Core.Models;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace GLaDOS.LLama;

/// <summary>
/// Represents a language model implementation based on the LLaMA architecture.
/// This class is responsible for loading, initializing, and generating responses using a LLaMA model.
/// It inherits from <see cref="LanguageModel"/> and implements <see cref="IDisposable"/> for resource management.
/// </summary>
public class LLamaLanguageModel : LanguageModel, IDisposable
{
    private const int DefaultMaxOutputTokens = 2048;
    private const int MinimumMaxOutputTokens = 256;
    private const int PromptReserveTokens = 512;

    private Grammar? _grammer;
    private readonly ModelParams _params;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private bool _isDisposed;
    private bool _initialized;

    public override LanguageModelMetaData ModelMetaData { get; }

    public LLamaLanguageModel(LanguageModelMetaData metaData, ModelParams @params)
    {
        _params = @params;
        ModelMetaData = metaData ?? throw new ArgumentNullException(nameof(metaData));
    }

    protected override Task OnInitializeAsync()
    {
        if (_initialized) return Task.CompletedTask;
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _weights = LLamaWeights.LoadFromFile(_params);
        _context = _weights.CreateContext(_params);

        var grammerFile = _params.ModelPath.Replace(".gguf", ".gbnf");
        if (File.Exists(grammerFile))
        {
            _grammer = new Grammar(File.ReadAllText(grammerFile), "root");
        }

        _initialized = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asynchronously generates a response based on the provided prompt and chat options.
    /// </summary>
    /// <param name="prompt">The input prompt to generate a response for.</param>
    /// <param name="chatOptions">Configuration options for the chat generation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A string containing the generated response.</returns>
    protected override async Task<string> OnGenerateResponseAsync(
        string prompt,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        var maxTokens = GetSafeMaxTokens(prompt, chatOptions.MaxTokenLength);
        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            OverflowStrategy = ContextOverflowStrategy.TruncateAndReprefill,
            ContextTruncationPercentage = 0.1f,
            SamplingPipeline = new DefaultSamplingPipeline()
            {
                Temperature = chatOptions.Temperature ?? 0.5f,
                Seed = (uint)Random.Shared.Next(1, 100000),
                Grammar = _grammer
            },
            AntiPrompts = chatOptions.StopSequences?.Count > 0
                ? chatOptions.StopSequences.ToArray()
                : ["<|im_end|>"],
        };

        var executor = new StatelessExecutor(_weights!, _params);
        var fullResponseBuilder = new StringBuilder();

        // 1. Verzamel de volledige output van de LLM als één string
        await foreach (var token in executor.InferAsync(prompt, inferenceParams, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            fullResponseBuilder.Append(token);
        }

        executor.Context.Dispose();

        return fullResponseBuilder.ToString().Trim();
    }

    private int GetSafeMaxTokens(string prompt, int? requestedMaxTokens)
    {
        int requested = requestedMaxTokens.GetValueOrDefault(ModelMetaData.MaxOutputTokens);
        if (requested <= 0)
        {
            requested = DefaultMaxOutputTokens;
        }

        int contextSize = _params.ContextSize.HasValue
            ? (int)Math.Min(_params.ContextSize.Value, int.MaxValue)
            : 0;
        if (contextSize <= 0)
        {
            return Math.Max(MinimumMaxOutputTokens, requested);
        }

        int estimatedPromptTokens = Math.Max(1, (int)Math.Ceiling(prompt.Length / 4.0));
        int availableOutputTokens = contextSize - estimatedPromptTokens - PromptReserveTokens;
        if (availableOutputTokens < MinimumMaxOutputTokens)
        {
            availableOutputTokens = MinimumMaxOutputTokens;
        }

        return Math.Clamp(requested, MinimumMaxOutputTokens, availableOutputTokens);
    }

    protected override Task OnUnloadAsync()
    {
        _context?.Dispose();
        _context = null;

        _weights?.Dispose();
        _weights = null;

        _grammer = null;
        _initialized = false;

        GC.Collect();
        GC.WaitForPendingFinalizers();

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        OnUnloadAsync().GetAwaiter().GetResult();
        _isDisposed = true;

        GC.SuppressFinalize(this);
    }
}
