using System.Runtime.CompilerServices;
using Jarvis.Core.Models;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Jarvis.LLama;

public class LLamaLanguageModel : LanguageModel, IDisposable
{
    private readonly string _modelPath;
    private readonly ModelParams _params;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private InteractiveExecutor? _executor; // Veranderd van ChatSession naar InteractiveExecutor
    private bool _isDisposed;

    public override LanguageModelMetaData ModelMetaData { get; }

    public LLamaLanguageModel(string modelPath, LanguageModelMetaData metaData, ModelParams @params)
    {
        _params = @params;
        _modelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
        ModelMetaData = metaData ?? throw new ArgumentNullException(nameof(metaData));
    }

    protected override Task OnInitializeAsync()
    {
        // De hardware configurator heeft _params al optimaal gevuld (runtimes, threads, GPU)
        _weights = LLamaWeights.LoadFromFile(_params);
        _context = _weights.CreateContext(_params);

        // We gebruiken direct de executor, omdat de controller de formattering beheert
        _executor = new InteractiveExecutor(_context);

        return Task.CompletedTask;
    }

    protected override async IAsyncEnumerable<(string Text, int Percent)> OnGenerateResponseAsync(
        string formattedPrompt, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_executor == null)
        {
            throw new InvalidOperationException("Het model is nog niet geïnitialiseerd. Roep eerst InitializeAsync() aan.");
        }

        // Configureer de stoptokens passend bij jouw controller template
        var inferenceParams = new InferenceParams()
        {
            MaxTokens = 1024,
            SamplingPipeline = new DefaultSamplingPipeline()
            {
                Temperature = 0.5f,
                Seed = 1337
            },
            // We voegen expliciet alle varianten toe, inclusief de newline-versies
            AntiPrompts = new List<string> 
            { 
                "<|end|>", 
                "<|end|>\n",
                "<|user|>", 
                "<|system|>", 
                "<|assistant|>",
                "User:", 
                "\nUser:" 
            }
        };

        // We sturen de 'formattedPrompt' (jouw gebouwde StringBuilder string) direct 1-op-1 naar LLamaSharp
        await foreach (var token in _executor.InferAsync(formattedPrompt, inferenceParams, cancellationToken))
        {
            yield return (token, 0);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _context?.Dispose();
        _weights?.Dispose();
        _isDisposed = true;
        
        GC.SuppressFinalize(this);
    }
}