using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GLaDOS.Core.Interfaces;
using GLaDOS.Core.Models;
using GLaDOS.Core.Services;
using GLaDOS.Models;
using Microsoft.AspNetCore.Mvc;

namespace GLaDOS.Endpoints;

public static class FimEndpoints
{
    private static readonly SemaphoreSlim LlmLock = new(1, 1);
    private static readonly Encoding SseEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly string[] DefaultFimStops =
    [
        "<|fim_prefix|>",
        "<|fim_suffix|>",
        "<|fim_middle|>",
        "<|fim_pad|>",
        "<|im_end|>"
    ];

    public static void MapFimEndpoints(this IEndpointRouteBuilder app)
    {
        var v1Group = app.MapGroup("/v1");
        v1Group.MapPost("/fim/completions", HandleFimCompletions);
    }

    private static async Task<IResult> HandleFimCompletions(
        [FromServices] IModelManager modelManager,
        [FromBody] FimCompletionRequest? request,
        HttpContext context)
    {
        var token = context.RequestAborted;
        if (request == null)
        {
            return Results.BadRequest(new { error = new { message = "Request body is required.", type = "invalid_request_error" } });
        }

        var prefix = request.Prefix ?? request.Prompt;
        if (prefix == null)
        {
            return Results.BadRequest(new { error = new { message = "Either 'prefix' or 'prompt' is required.", type = "invalid_request_error" } });
        }

        var suffix = request.Suffix ?? string.Empty;
        var stops = BuildStopSequences(request);

        try
        {
            await LlmLock.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }

        LanguageModel model;
        try
        {
            model = await modelManager.GetAndInitializeModel(request.Model);
        }
        catch (Exception ex)
        {
            LlmLock.Release();
            return Results.Problem(detail: $"Failed to load model: {ex.Message}", statusCode: 500);
        }

        var responseId = $"fimcmpl-{Guid.NewGuid():n}";
        var createdTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var modelName = request.Model ?? model.ModelMetaData.Id;
        var prompt = BuildQwenFimPrompt(prefix, suffix);
        var options = new ChatOptions
        {
            Temperature = request.Temperature,
            MaxTokenLength = request.MaxCompletionTokens ?? request.MaxTokenLength,
            StopSequences = stops
        };

        if (!request.Stream)
        {
            try
            {
                var generatedText = await model.GenerateResponseAsync(prompt, options, token);
                generatedText = TrimAtStopSequence(generatedText, stops);

                return Results.Ok(new FimCompletionResponse
                {
                    Id = responseId,
                    Created = createdTimestamp,
                    Model = modelName,
                    Choices =
                    [
                        new FimCompletionChoice
                        {
                            Index = 0,
                            Text = generatedText,
                            FinishReason = "stop"
                        }
                    ]
                });
            }
            finally
            {
                LlmLock.Release();
            }
        }

        var serializerOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        return Results.Stream(async stream =>
        {
            try
            {
                await using var writer = new StreamWriter(stream, SseEncoding, leaveOpen: true) { NewLine = "\n" };
                var generatedText = await model.GenerateResponseAsync(prompt, options, token);
                generatedText = TrimAtStopSequence(generatedText, stops);

                await WriteSseAsync(
                    writer,
                    CreateChunk(responseId, createdTimestamp, modelName, generatedText),
                    serializerOptions,
                    token);

                await WriteSseAsync(
                    writer,
                    CreateChunk(responseId, createdTimestamp, modelName, string.Empty, "stop"),
                    serializerOptions,
                    token);

                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync(token);
            }
            finally
            {
                LlmLock.Release();
            }
        }, "text/event-stream");
    }

    private static string BuildQwenFimPrompt(string prefix, string suffix)
    {
        return $"<|fim_prefix|>{prefix}<|fim_suffix|>{suffix}<|fim_middle|>";
    }

    private static IReadOnlyList<string> BuildStopSequences(FimCompletionRequest request)
    {
        var stops = new List<string>(DefaultFimStops);
        if (request.Stop is not { } stopElement)
        {
            return stops;
        }

        if (stopElement.ValueKind == JsonValueKind.String)
        {
            var stop = stopElement.GetString();
            if (!string.IsNullOrEmpty(stop))
            {
                stops.Add(stop);
            }
        }
        else if (stopElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in stopElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var stop = item.GetString();
                if (!string.IsNullOrEmpty(stop))
                {
                    stops.Add(stop);
                }
            }
        }

        return stops.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string TrimAtStopSequence(string text, IReadOnlyList<string> stops)
    {
        var stopIndex = -1;
        foreach (var stop in stops)
        {
            var index = text.IndexOf(stop, StringComparison.Ordinal);
            if (index >= 0 && (stopIndex < 0 || index < stopIndex))
            {
                stopIndex = index;
            }
        }

        return stopIndex >= 0 ? text[..stopIndex] : text;
    }

    private static FimCompletionChunk CreateChunk(
        string id,
        long created,
        string model,
        string text,
        string? finishReason = null)
    {
        return new FimCompletionChunk
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new FimCompletionChunkChoice
                {
                    Index = 0,
                    Text = text,
                    FinishReason = finishReason
                }
            ]
        };
    }

    private static async Task WriteSseAsync(
        StreamWriter writer,
        FimCompletionChunk chunk,
        JsonSerializerOptions serializerOptions,
        CancellationToken token)
    {
        var json = JsonSerializer.Serialize(chunk, serializerOptions);
        await writer.WriteAsync($"data: {json}\n\n");
        await writer.FlushAsync(token);
    }
}
