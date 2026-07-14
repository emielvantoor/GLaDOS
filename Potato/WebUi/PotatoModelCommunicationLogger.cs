using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
        ChatResponse response)
    {
        PotatoConsole.EventSink?.Record(
            "model-exchange",
            "model",
            FormatModelExchange(requestId, messages, options, response),
            collapsed: true);
    }

    private static void RecordModelExchange(
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

    private static string FormatModelExchange(
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

    private static string FormatModelExchange(
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
