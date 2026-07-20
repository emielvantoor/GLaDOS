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
public class LLamaLanguageModel : LanguageModel, IModelSessionUsageProvider, IDisposable
{
    private const int DefaultMaxOutputTokens = 2048;
    private const int MinimumMaxOutputTokens = 256;
    private const int PromptReserveTokens = 512;
    // A context reserves GPU KV-cache memory for its full configured window. Keep this
    // deliberately small: clients can always rebuild from their OpenAI message history.
    private const int MaximumInteractiveSessions = 2;
    // Potato keeps an interactive session alive with a one-second heartbeat.  This
    // short timeout releases the GPU KV cache promptly when its process is stopped
    // by the debugger or terminates without sending the normal DELETE request.
    private static readonly TimeSpan InteractiveSessionLifetime = TimeSpan.FromSeconds(10);

    private Grammar? _grammer;
    private readonly ModelParams _params;
    private LLamaWeights? _weights;
    private readonly Dictionary<string, InteractiveSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
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

        var fullResponseBuilder = new StringBuilder();
        string? sessionId = NormalizeSessionId(chatOptions.SessionId);

        if (sessionId is null)
        {
            var executor = new StatelessExecutor(_weights!, _params);
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                fullResponseBuilder.Append(token);
            }

            executor.Context.Dispose();

            return fullResponseBuilder.ToString().Trim();
        }

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            InteractiveSession session = GetOrCreateSession(sessionId);
            string input = GetIncrementalInput(session, prompt, maxTokens);

            await foreach (var token in session.Executor.InferAsync(input, inferenceParams, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                fullResponseBuilder.Append(token);
            }

            session.LastPrompt = prompt;
            session.LastResponse = fullResponseBuilder.ToString();
            session.EstimatedTokens += EstimateTokens(input.Length + session.LastResponse.Length);
            session.LastActivityAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _sessionLock.Release();
        }

        Console.WriteLine($"Generated {fullResponseBuilder.Length}/{maxTokens} tokens");

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
        foreach (InteractiveSession session in _sessions.Values)
        {
            session.Context.Dispose();
        }

        _sessions.Clear();

        _weights?.Dispose();
        _weights = null;

        _grammer = null;
        _initialized = false;

        GC.Collect();
        GC.WaitForPendingFinalizers();

        return Task.CompletedTask;
    }

    private InteractiveSession GetOrCreateSession(string sessionId)
    {
        PruneInactiveSessions();

        if (_sessions.TryGetValue(sessionId, out InteractiveSession? session))
        {
            return session;
        }

        var context = _weights!.CreateContext(_params);
        session = new InteractiveSession(context, new InteractiveExecutor(context));
        _sessions.Add(sessionId, session);
        PruneInactiveSessions();
        return session;
    }

    public ModelSessionUsage? GetSessionUsage(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        _sessionLock.Wait();
        try
        {
            PruneInactiveSessions();
            if (!_sessions.TryGetValue(sessionId.Trim(), out InteractiveSession? session)) return null;

            int contextSize = _params.ContextSize.HasValue ? (int)_params.ContextSize.Value : 0;
            return new ModelSessionUsage(sessionId.Trim(), session.EstimatedTokens, contextSize, session.LastActivityAt);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public bool TouchSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;

        _sessionLock.Wait();
        try
        {
            if (!_sessions.TryGetValue(sessionId.Trim(), out InteractiveSession? session)) return false;

            session.LastActivityAt = DateTimeOffset.UtcNow;
            return true;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public bool ReleaseSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;

        _sessionLock.Wait();
        try
        {
            if (!_sessions.Remove(sessionId.Trim(), out InteractiveSession? session)) return false;

            session.Context.Dispose();
            return true;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public int ReleaseInactiveSessions()
    {
        _sessionLock.Wait();
        try
        {
            int countBefore = _sessions.Count;
            PruneInactiveSessions();
            return countBefore - _sessions.Count;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private void PruneInactiveSessions()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - InteractiveSessionLifetime;
        foreach ((string id, InteractiveSession session) in _sessions.ToArray())
        {
            if (session.LastActivityAt >= cutoff) continue;

            session.Context.Dispose();
            _sessions.Remove(id);
        }

        foreach ((string id, InteractiveSession session) in _sessions
                     .OrderBy(pair => pair.Value.LastActivityAt)
                     .Take(Math.Max(0, _sessions.Count - MaximumInteractiveSessions))
                     .ToArray())
        {
            session.Context.Dispose();
            _sessions.Remove(id);
        }
    }

    private string GetIncrementalInput(InteractiveSession session, string prompt, int maxTokens)
    {
        if (session.LastPrompt is null || session.LastResponse is null)
        {
            return prompt;
        }

        string processedPrefix = session.LastPrompt + session.LastResponse;
        int contextSize = _params.ContextSize.HasValue ? (int)_params.ContextSize.Value : int.MaxValue;
        int deltaTokens = EstimateTokens(prompt.Length - processedPrefix.Length);
        if (prompt.StartsWith(processedPrefix, StringComparison.Ordinal) &&
            session.EstimatedTokens + deltaTokens + maxTokens <= contextSize)
        {
            return prompt[processedPrefix.Length..];
        }

        // The client replayed or changed the conversation (common after tool calls and edits).
        // Rebuild from its authoritative, bounded message history instead of appending duplicates.
        session.Context.Dispose();
        var context = _weights!.CreateContext(_params);
        session.Context = context;
        session.Executor = new InteractiveExecutor(context);
        session.LastPrompt = null;
        session.LastResponse = null;
        session.EstimatedTokens = 0;
        return prompt;
    }

    private static string? NormalizeSessionId(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();

    private static int EstimateTokens(int characterCount) => Math.Max(1, (int)Math.Ceiling(characterCount / 4d));

    private sealed class InteractiveSession(LLamaContext context, InteractiveExecutor executor)
    {
        public LLamaContext Context { get; set; } = context;
        public InteractiveExecutor Executor { get; set; } = executor;
        public string? LastPrompt { get; set; }
        public string? LastResponse { get; set; }
        public int EstimatedTokens { get; set; }
        public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        OnUnloadAsync().GetAwaiter().GetResult();
        _isDisposed = true;

        GC.SuppressFinalize(this);
    }
}
