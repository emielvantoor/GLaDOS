using System.Text;
using GLaDOS.Core.Interfaces;
using GLaDOS.Core.Models;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace GLaDOS.LLama;

public class LLamaLanguageModel : LanguageModel, IDisposable
{
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

    protected override async Task<string> OnGenerateResponseAsync(
        string prompt,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        var inferenceParams = new InferenceParams
        {
            MaxTokens = chatOptions.MaxTokenLength ?? ModelMetaData.MaxOutputTokens,
            SamplingPipeline = new DefaultSamplingPipeline()
            {
                Temperature = chatOptions.Temperature ?? 0.5f,
                Seed = (uint)Random.Shared.Next(1, 100000),
                Grammar = _grammer
            },
            AntiPrompts = ["<|im_end|>"],
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
