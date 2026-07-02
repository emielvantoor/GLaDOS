using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jarvis.Core.Agents;
using Jarvis.Core.Interfaces;
using Jarvis.Core.Models;
using Jarvis.Core.Services;
using Jarvis.Extensions;
using Jarvis.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Jarvis.Endpoints;

public static class OpenAiEndpoints
{
    private static readonly SemaphoreSlim _llmLock = new(1, 1);
    private static readonly Encoding SseEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void MapOpenAiEndpoints(this IEndpointRouteBuilder app)
    {
        var v1Group = app.MapGroup("/v1");

        v1Group.MapGet("/models/{model}", GetSpecificModel);
        v1Group.MapGet("/models", GetModels);
        v1Group.MapPost("/chat/completions", HandleChatCompletions);
    }

    /// <summary>
    /// Handles OpenAI compliant chat completions supporting standard text and tool-call SSE streaming.
    /// </summary>
    private static async Task<IResult> HandleChatCompletions(
        [FromServices] IModelManager modelManager,
        [FromServices] JarvisAgent agent,
        [FromBody] ChatCompletionRequest? request,
        HttpContext context)
    {
        var token = context.RequestAborted;

        // 1. Basic Validation
        if (request?.Messages == null || !request.Messages.Any())
        {
            return Results.BadRequest(new { error = new { message = "Invalid request or empty messages list.", type = "invalid_request_error" } });
        }

        // 2. Concurrency Lock
        try
        {
            await _llmLock.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }

        Console.WriteLine($"Handling Chat Completions for model: {request.Model}");

        // 3. Initialize LLM Model
        LanguageModel model;
        try
        {
            model = await modelManager.GetAndInitializeModel(request.Model);
        }
        catch (Exception ex)
        {
            _llmLock.Release();
            return Results.Problem(detail: $"Failed to load model: {ex.Message}", statusCode: 500);
        }

        // 4. Transform to internal domain architecture
        var domainMessages = request.Messages.Select(message => message.ToDomainModel()).ToList();
        var domainTools = request.Tools?.Select(tool => tool.ToDomainModel()).ToList() ?? [];

        var agentResultStream = agent.RunAsync(model, domainMessages, new ChatOptions
        {
            Temperature = request.Temperature,
            MaxTokenLength = request.MaxCompletionTokens ?? request.MaxTokenLength
        }, domainTools, token);

        var serializerOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Metadata setup for OpenAI specifications
        string chunkId = $"chatcmpl-{Guid.NewGuid()}";
        long createdTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string modelName = request.Model ?? "local-model";

        if (!request.Stream)
        {
            return await CreateBufferedResponse(
                agentResultStream,
                chunkId,
                createdTimestamp,
                modelName,
                token);
        }

        // 5. Response SSE Stream
        return Results.Stream(async stream =>
        {
            try
            {
                await using var writer = new StreamWriter(stream, SseEncoding, leaveOpen: true) { NewLine = "\n" };
                bool toolCallTriggered = false;

                await WriteSseAsync(
                    writer,
                    CreateChunk(chunkId, createdTimestamp, modelName, new ChatDelta { Role = "assistant" }),
                    serializerOptions,
                    token);

                await foreach (var text in agentResultStream.WithCancellation(token))
                {
                    if (token.IsCancellationRequested) break;

                    if (IsInternalStatusMessage(text))
                    {
                        continue;
                    }
                
                    // Intercept and parse internal legacy __TOOL_CALL__ syntax
                    if (TryParseToolCall(text, out var toolCall))
                    {
                        toolCallTriggered = true;
                        await WriteSseAsync(
                            writer,
                            CreateChunk(
                                chunkId,
                                createdTimestamp,
                                modelName,
                                new ChatDelta { ToolCalls = [CreateStreamingToolCall(toolCall, 0)] }),
                            serializerOptions,
                            token);
                        await WriteSseAsync(
                            writer,
                            CreateChunk(
                                chunkId,
                                createdTimestamp,
                                modelName,
                                new ChatDelta(),
                                "tool_calls"),
                            serializerOptions,
                            token);
                        break; 
                    }

                    // Handle Standard Text Token Stream
                    await WriteSseAsync(
                        writer,
                        CreateChunk(chunkId, createdTimestamp, modelName, new ChatDelta { Content = text }),
                        serializerOptions,
                        token);
                }

                // Finalize text response chunk if a tool call was not triggered
                if (!toolCallTriggered && !token.IsCancellationRequested)
                {
                    await WriteSseAsync(
                        writer,
                        CreateChunk(chunkId, createdTimestamp, modelName, new ChatDelta(), "stop"),
                        serializerOptions,
                        token);
                }

                // Standard end of SSE protocol payload
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync(token);
            }
            finally
            {
                Console.WriteLine("Complete response generated via stream.");
                _llmLock.Release();
            }
        }, "text/event-stream");
    }

    private static async Task<IResult> CreateBufferedResponse(
        IAsyncEnumerable<string> agentResultStream,
        string responseId,
        long createdTimestamp,
        string modelName,
        CancellationToken token)
    {
        var responseBuffer = new StringBuilder();

        try
        {
            await foreach (var text in agentResultStream.WithCancellation(token))
            {
                if (IsInternalStatusMessage(text))
                {
                    continue;
                }

                if (TryParseToolCall(text, out var toolCall))
                {
                    return Results.Ok(new ChatCompletionResponse
                    {
                        Id = responseId,
                        Created = createdTimestamp,
                        Model = modelName,
                        Choices =
                        [
                            new ChatChoice
                            {
                                Message = new ChatMessage
                                {
                                    Role = "assistant",
                                    ToolCalls = [toolCall]
                                },
                                FinishReason = "tool_calls"
                            }
                        ]
                    });
                }

                responseBuffer.Append(text);
            }

            return Results.Ok(new ChatCompletionResponse
            {
                Id = responseId,
                Created = createdTimestamp,
                Model = modelName,
                Choices =
                [
                    new ChatChoice
                    {
                        Message = new ChatMessage
                        {
                            Role = "assistant",
                            Content = responseBuffer.ToString()
                        },
                        FinishReason = "stop"
                    }
                ]
            });
        }
        finally
        {
            Console.WriteLine("Complete response generated.");
            _llmLock.Release();
        }
    }

    private static ChatCompletionChunk CreateChunk(
        string id,
        long created,
        string model,
        ChatDelta delta,
        string? finishReason = null)
    {
        return new ChatCompletionChunk
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new ChatChunkChoice
                {
                    Delta = delta,
                    FinishReason = finishReason
                }
            ]
        };
    }

    private static async Task WriteSseAsync(
        StreamWriter writer,
        ChatCompletionChunk chunk,
        JsonSerializerOptions serializerOptions,
        CancellationToken token)
    {
        var json = JsonSerializer.Serialize(chunk, serializerOptions);
        await writer.WriteAsync($"data: {json}\n\n");
        await writer.FlushAsync(token);
    }

    private static bool TryParseToolCall(string? text, out ChatCompletionToolCall toolCall)
    {
        toolCall = new ChatCompletionToolCall();

        if (string.IsNullOrEmpty(text) || !text.StartsWith("__TOOL_CALL__:", StringComparison.Ordinal))
        {
            return false;
        }

        var payload = text["__TOOL_CALL__:".Length..];
        var separatorIndex = payload.IndexOf('|');

        toolCall = new ChatCompletionToolCall
        {
            Id = $"call_{Guid.NewGuid():n}",
            Type = "function",
            Function = new ChatCompletionToolCallFunction
            {
                Name = separatorIndex >= 0 ? payload[..separatorIndex] : payload,
                Arguments = separatorIndex >= 0 ? payload[(separatorIndex + 1)..] : "{}"
            }
        };

        return true;
    }

    private static ChatCompletionToolCall CreateStreamingToolCall(ChatCompletionToolCall toolCall, int index)
    {
        return new ChatCompletionToolCall
        {
            Index = index,
            Id = toolCall.Id,
            Type = toolCall.Type,
            Function = toolCall.Function
        };
    }

    private static bool IsInternalStatusMessage(string? text)
    {
        return !string.IsNullOrWhiteSpace(text)
               && text.TrimStart().StartsWith("[Systeem:", StringComparison.Ordinal);
    }

    private static IResult GetModels([FromServices] IModelManager modelManager)
    {
        var modelsList = new ModelListResponse();
        var models = modelManager.GetAvailableModels();
        modelsList.Data.AddRange(models.Select(m => m.ToDto()));

        return Results.Ok(modelsList);
    }

    private static IResult GetSpecificModel([FromRoute] string model, [FromServices] IModelManager modelManager)
    {
        var models = modelManager.GetAvailableModels();
        var dtoModel = models.FirstOrDefault(m => m.Id == model)?.ToDto();
        
        if (dtoModel == null)
        {
            return Results.NotFound(new { error = new { message = $"Model '{model}' not found.", type = "invalid_request_error" } });
        }
        
        return Results.Ok(dtoModel);
    }
}
