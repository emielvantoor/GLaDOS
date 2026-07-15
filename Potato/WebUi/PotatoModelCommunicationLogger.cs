using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Potato.WebUi;

internal sealed class PotatoModelCommunicationLogger(
    IChatClient innerClient,
    int contextSize,
    ExecutionMemory? executionMemory = null) : DelegatingChatClient(innerClient)
{
    private const int DefaultMaxOutputTokens = 4096;
    private static readonly AsyncLocal<int> MainPotatoChatContextDepth = new();

    private long nextRequestId;

    public static IDisposable TrackMainPotatoChatContext()
    {
        MainPotatoChatContextDepth.Value++;
        return new MainPotatoChatContextScope();
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatMessage[] capturedMessages = messages.ToArray();
        long requestId = Interlocked.Increment(ref nextRequestId);
        RecordMainPotatoChatContextUsage(capturedMessages, options);

        ChatResponse response = await base.GetResponseAsync(capturedMessages, options, cancellationToken);
        RecordModelExchange(requestId, capturedMessages, options, response);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatMessage[] capturedMessages = messages.ToArray();
        long requestId = Interlocked.Increment(ref nextRequestId);
        RecordMainPotatoChatContextUsage(capturedMessages, options);

        var response = new StringBuilder();
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(capturedMessages, options, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            response.Append(update.Text);
            yield return update;
        }

        RecordModelExchange(requestId, capturedMessages, options, response.ToString());
    }

    private void RecordMainPotatoChatContextUsage(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        if (MainPotatoChatContextDepth.Value <= 0)
        {
            return;
        }

        ContextUsage? usage = GetContextUsage(messages, options, responseText: null);
        if (usage is null)
        {
            return;
        }

        PotatoConsole.EventSink?.RecordContextUsage(
            usage.PromptTokens,
            usage.ContextSize,
            FormatPercentValue(usage.PromptTokens, usage.ContextSize),
            usage.MaxOutputTokens,
            usage.HeadroomAfterReservedOutput,
            usage.ExceedsContext,
            FormatContextUsageSummary(usage));
    }

    private void RecordModelExchange(
        long requestId,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        ChatResponse response)
    {
        PotatoConsole.EventSink?.Record(
            "model-exchange",
            "model",
            FormatModelExchange(requestId, messages, options, response),
            collapsed: true);
    }

    private void RecordModelExchange(
        long requestId,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        string responseText)
    {
        PotatoConsole.EventSink?.Record(
            "model-exchange",
            "model",
            FormatModelExchange(requestId, messages, options, responseText),
            collapsed: true);
    }

    private string FormatModelExchange(
        long requestId,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        ChatResponse response)
    {
        string responseText = FormatMessages(response.Messages);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            responseText = string.IsNullOrWhiteSpace(response.Text) ? "(empty)" : response.Text;
        }

        return FormatModelExchange(requestId, messages, options, responseText);
    }

    private string FormatModelExchange(
        long requestId,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        string responseText)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"step: {PotatoConsole.ActiveProgressMessage ?? $"Potato model call #{requestId}"}");
        builder.AppendLine();
        builder.AppendLine("## Request");
        AppendOptions(builder, options);
        AppendContextUsage(builder, messages, options, responseText);
        builder.AppendLine();

        for (int i = 0; i < messages.Count; i++)
        {
            ChatMessage message = messages[i];
            builder.AppendLine($"## {i + 1}. {message.Role}");
            builder.AppendLine(FormatMessageContent(message));
            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("## Response");
        builder.AppendLine(string.IsNullOrWhiteSpace(responseText) ? "(empty)" : responseText);
        return builder.ToString().TrimEnd();
    }

    private void AppendContextUsage(
        StringBuilder builder,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        string responseText)
    {
        if (MainPotatoChatContextDepth.Value <= 0)
        {
            return;
        }

        ContextUsage? usage = GetContextUsage(messages, options, responseText);
        if (usage is null)
        {
            return;
        }

        builder.AppendLine("## Context");
        builder.AppendLine(
            $"estimated prompt (chat history): {FormatNumber(usage.PromptTokens)} / {FormatNumber(usage.ContextSize)} tokens " +
            $"({FormatPercent(usage.PromptTokens, usage.ContextSize)} used)");
        
        // Show ExecutionMemory optimization metrics if available
        if (executionMemory is not null && executionMemory.Count > 0)
        {
            var metrics = CalculateOptimizationMetrics(messages);
            if (metrics.HasData)
            {
                builder.AppendLine($"  - Execution memory (full data): {FormatNumber(metrics.ExecutionMemoryCharacters)} chars " +
                    $"(~{FormatNumber(metrics.ExecutionMemoryEstimatedTokens)} est. tokens, not in chat)");
                builder.AppendLine($"  - Truncations: {metrics.TruncationCount} items, " +
                    $"recovered {FormatNumber(metrics.TotalBytesRecovered)} bytes");
                if (metrics.TokensSavedPercentage > 0)
                {
                    builder.AppendLine($"  - Token efficiency: +{metrics.TokensSavedPercentage:F1}% extra data available");
                }
            }
        }
        
        builder.AppendLine($"available before output: {FormatNumber(usage.AvailableBeforeOutput)} tokens");
        builder.AppendLine($"reserved output ({usage.OutputSource}): {FormatNumber(usage.MaxOutputTokens)} tokens");
        builder.AppendLine($"headroom after reserved output: {FormatNumber(usage.HeadroomAfterReservedOutput)} tokens");
        builder.AppendLine($"estimated response: {FormatNumber(usage.ResponseTokens)} tokens");

        if (usage.ExceedsContext)
        {
            builder.AppendLine("warning: estimated prompt plus reserved output exceeds the configured context window.");
        }
    }

    private ContextUsage? GetContextUsage(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        string? responseText)
    {
        if (contextSize <= 0)
        {
            return null;
        }

        int promptTokens = EstimateMessageTokens(messages);
        int responseTokens = EstimateTokenCount(responseText ?? string.Empty);
        int maxOutputTokens = options?.MaxOutputTokens ?? DefaultMaxOutputTokens;
        int availableBeforeOutput = Math.Max(0, contextSize - promptTokens);
        int headroomAfterReservedOutput = Math.Max(0, contextSize - promptTokens - maxOutputTokens);
        string outputSource = options?.MaxOutputTokens.HasValue == true ? "requested" : "default";

        return new ContextUsage(
            contextSize,
            promptTokens,
            responseTokens,
            maxOutputTokens,
            outputSource,
            availableBeforeOutput,
            headroomAfterReservedOutput);
    }

    private static string FormatMessages(IList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (int i = 0; i < messages.Count; i++)
        {
            ChatMessage message = messages[i];
            builder.AppendLine($"## {i + 1}. {message.Role}");
            builder.AppendLine(FormatMessageContent(message));
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatMessageContent(ChatMessage message)
    {
        if (message.Contents.Count == 0)
        {
            return string.IsNullOrWhiteSpace(message.Text) ? "(empty)" : message.Text;
        }

        var builder = new StringBuilder();
        foreach (AIContent content in message.Contents)
        {
            AppendContent(builder, content);
        }

        string rendered = builder.ToString().TrimEnd();
        return string.IsNullOrWhiteSpace(rendered) ? "(empty)" : rendered;
    }

    private static int EstimateMessageTokens(IReadOnlyList<ChatMessage> messages)
    {
        int total = 0;
        foreach (ChatMessage message in messages)
        {
            total += EstimateTokenCount($"{message.Role}\n{FormatMessageContent(message)}");
        }

        return total;
    }

    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return (int)Math.Ceiling(text.Length / 4.0);
    }

    private static string FormatNumber(int value) => value.ToString("N0");

    private static string FormatPercent(int used, int total)
    {
        if (total <= 0)
        {
            return "0%";
        }

        return $"{FormatPercentValue(used, total):0.#}%";
    }

    private static double FormatPercentValue(int used, int total) =>
        total <= 0 ? 0 : Math.Min(100.0, used * 100.0 / total);

    private static string FormatContextUsageSummary(ContextUsage usage)
    {
        string warning = usage.ExceedsContext ? ", warning" : string.Empty;
        return
            $"({FormatNumber(usage.PromptTokens)}/{FormatNumber(usage.ContextSize)} {FormatPercent(usage.PromptTokens, usage.ContextSize)}, " +
            $"output {FormatNumber(usage.MaxOutputTokens)}, headroom {FormatNumber(usage.HeadroomAfterReservedOutput)}{warning})";
    }

    private sealed class MainPotatoChatContextScope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            MainPotatoChatContextDepth.Value = Math.Max(0, MainPotatoChatContextDepth.Value - 1);
        }
    }

    private sealed record ContextUsage(
        int ContextSize,
        int PromptTokens,
        int ResponseTokens,
        int MaxOutputTokens,
        string OutputSource,
        int AvailableBeforeOutput,
        int HeadroomAfterReservedOutput)
    {
        public bool ExceedsContext => PromptTokens + MaxOutputTokens > ContextSize;
    }

    private sealed record OptimizationMetrics(
        int ExecutionMemoryCharacters,
        int ExecutionMemoryEstimatedTokens,
        int TruncationCount,
        int TotalBytesRecovered,
        double TokensSavedPercentage)
    {
        public bool HasData => TruncationCount > 0 || ExecutionMemoryCharacters > 0;
    }

    private OptimizationMetrics CalculateOptimizationMetrics(IReadOnlyList<ChatMessage> messages)
    {
        if (executionMemory is null || executionMemory.Count == 0)
        {
            return new OptimizationMetrics(0, 0, 0, 0, 0);
        }

        var (totalMemoryCharacters, truncatedItems, totalBytesRecovered) = executionMemory.GetMetrics();
        
        int chatHistoryTokens = EstimateMessageTokens(messages);
        int memoryEstimatedTokens = totalMemoryCharacters > 0 ? (int)Math.Ceiling(totalMemoryCharacters / 4.0) : 0;
        double tokensSaved = chatHistoryTokens > 0 ? (totalBytesRecovered / 4.0) / chatHistoryTokens * 100.0 : 0;

        return new OptimizationMetrics(
            totalMemoryCharacters,
            memoryEstimatedTokens,
            truncatedItems,
            totalBytesRecovered,
            tokensSaved);
    }

    private static void AppendContent(StringBuilder builder, AIContent content)
    {
        switch (content)
        {
            case TextContent text:
                if (!string.IsNullOrWhiteSpace(text.Text))
                {
                    builder.AppendLine(text.Text);
                }
                break;

            case FunctionCallContent functionCall:
                builder.AppendLine($"Function call: {functionCall.Name} ({functionCall.CallId})");
                AppendJson(builder, functionCall.Arguments);
                if (functionCall.Exception is not null)
                {
                    builder.AppendLine($"Function call parse error: {functionCall.Exception.Message}");
                }
                break;

            case FunctionResultContent functionResult:
                builder.AppendLine($"Function result: {functionResult.CallId}");
                AppendJson(builder, functionResult.Result);
                if (functionResult.Exception is not null)
                {
                    builder.AppendLine($"Function error: {functionResult.Exception.Message}");
                }
                break;

            case ErrorContent error:
                builder.AppendLine($"Error: {error.Message}");
                if (!string.IsNullOrWhiteSpace(error.ErrorCode))
                {
                    builder.AppendLine($"Code: {error.ErrorCode}");
                }
                break;

            case DataContent data:
                builder.AppendLine($"Data: {data.MediaType}, {data.Name ?? data.Uri}");
                break;

            case UsageContent usage:
                builder.AppendLine($"Usage: {usage.Details}");
                break;

            default:
                builder.AppendLine($"{content.GetType().Name}: {content}");
                break;
        }
    }

    private static void AppendJson(StringBuilder builder, object? value)
    {
        if (value is null)
        {
            builder.AppendLine("(null)");
            return;
        }

        try
        {
            builder.AppendLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            builder.AppendLine(value.ToString());
        }
    }

    private static void AppendOptions(StringBuilder builder, ChatOptions? options)
    {
        if (options is null)
        {
            return;
        }

        var optionParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ModelId))
        {
            optionParts.Add($"model={options.ModelId}");
        }

        if (options.Temperature.HasValue)
        {
            optionParts.Add($"temperature={options.Temperature.Value:0.###}");
        }

        if (options.MaxOutputTokens.HasValue)
        {
            optionParts.Add($"maxOutputTokens={options.MaxOutputTokens.Value}");
        }

        if (options.ResponseFormat is not null)
        {
            optionParts.Add($"responseFormat={options.ResponseFormat.GetType().Name}");
        }

        if (optionParts.Count > 0)
        {
            builder.AppendLine(string.Join(", ", optionParts));
        }
    }
}
