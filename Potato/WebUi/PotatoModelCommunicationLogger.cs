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
        RecordModelRequest(requestId, capturedMessages, options);

        ChatResponse response = await base.GetResponseAsync(capturedMessages, options, cancellationToken);
        RecordModelResponse(requestId, response.Text);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatMessage[] capturedMessages = messages.ToArray();
        long requestId = Interlocked.Increment(ref nextRequestId);
        RecordModelRequest(requestId, capturedMessages, options);

        var response = new StringBuilder();
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(capturedMessages, options, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            response.Append(update.Text);
            yield return update;
        }

        RecordModelResponse(requestId, response.ToString());
    }

    private static void RecordModelRequest(long requestId, IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        PotatoConsole.EventSink?.Record(
            "model-request",
            "model",
            FormatModelRequest($"Potato model request #{requestId}", messages, options),
            collapsed: true);
    }

    private static void RecordModelResponse(long requestId, string response)
    {
        PotatoConsole.EventSink?.Record(
            "model-response",
            "model",
            FormatModelResponse($"Potato model response #{requestId}", response),
            collapsed: true);
    }

    private static string FormatModelRequest(string title, IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var builder = new StringBuilder();
        builder.AppendLine(title);
        AppendOptions(builder, options);
        builder.AppendLine();

        for (int i = 0; i < messages.Count; i++)
        {
            ChatMessage message = messages[i];
            builder.AppendLine($"## {i + 1}. {message.Role}");
            builder.AppendLine(string.IsNullOrWhiteSpace(message.Text) ? "(empty)" : message.Text);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatModelResponse(string title, string response)
    {
        var builder = new StringBuilder();
        builder.AppendLine(title);
        builder.AppendLine();
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
