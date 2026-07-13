using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace Potato.WebUi;

internal sealed class PotatoModelCommunicationLogger(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    private long nextRequestId;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatMessage[] capturedMessages = messages.ToArray();
        long requestId = Interlocked.Increment(ref nextRequestId);

        ChatResponse response = await base.GetResponseAsync(capturedMessages, options, cancellationToken);
        RecordModelExchange(requestId, capturedMessages, options, response.Text);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatMessage[] capturedMessages = messages.ToArray();
        long requestId = Interlocked.Increment(ref nextRequestId);

        var response = new StringBuilder();
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(capturedMessages, options, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            response.Append(update.Text);
            yield return update;
        }

        RecordModelExchange(requestId, capturedMessages, options, response.ToString());
    }

    private static void RecordModelExchange(
        long requestId,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        string response)
    {
        PotatoConsole.EventSink?.Record(
            "model-exchange",
            "model",
            FormatModelExchange(requestId, messages, options, response),
            collapsed: true);
    }

    private static string FormatModelExchange(
        long requestId,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        string response)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"step: {PotatoConsole.ActiveProgressMessage ?? $"Potato model call #{requestId}"}");
        builder.AppendLine();
        builder.AppendLine("## Request");
        AppendOptions(builder, options);
        builder.AppendLine();

        for (int i = 0; i < messages.Count; i++)
        {
            ChatMessage message = messages[i];
            builder.AppendLine($"## {i + 1}. {message.Role}");
            builder.AppendLine(string.IsNullOrWhiteSpace(message.Text) ? "(empty)" : message.Text);
            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("## Response");
        builder.AppendLine(string.IsNullOrWhiteSpace(response) ? "(empty)" : response);
        return builder.ToString().TrimEnd();
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
