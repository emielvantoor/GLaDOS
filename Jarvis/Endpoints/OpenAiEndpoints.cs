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
            MaxTokenLength = request.MaxTokenLength
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

        // 5. Response SSE Stream
        return Results.Stream(async stream =>
        {
            var completeResponseBuffer = new StringBuilder();
            
            try
            {
                await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { NewLine = "\n" };
                bool toolCallTriggered = false;

                await foreach (var text in agentResultStream.WithCancellation(token))
                {
                    if (token.IsCancellationRequested) break;

                    completeResponseBuffer.Append(text);
                
                    // Intercept and parse internal legacy __TOOL_CALL__ syntax
                    if (!string.IsNullOrEmpty(text) && text.StartsWith("__TOOL_CALL__:"))
                    {
                        toolCallTriggered = true;
                        var payload = text["__TOOL_CALL__:".Length..];
                        var separatorIndex = payload.IndexOf('|');
                    
                        string toolName = separatorIndex >= 0 ? payload[..separatorIndex] : payload;
                        string toolArgs = separatorIndex >= 0 ? payload[(separatorIndex + 1)..] : "{}";

                        var toolCallChunk = new
                        {
                            id = chunkId,
                            @object = "chat.completion.chunk",
                            created = createdTimestamp,
                            model = modelName,
                            choices = new[]
                            {
                                new
                                {
                                    index = 0,
                                    delta = new
                                    {
                                        tool_calls = new[]
                                        {
                                            new
                                            {
                                                index = 0,
                                                id = $"call_{Guid.NewGuid():n}",
                                                type = "function",
                                                function = new { name = toolName, arguments = toolArgs }
                                            }
                                        }
                                    },
                                    finish_reason = "tool_calls"
                                }
                            }
                        };

                        var jsonTool = JsonSerializer.Serialize(toolCallChunk, serializerOptions);
                        await writer.WriteAsync($"data: {jsonTool}\n\n");
                        await writer.FlushAsync(token);
                        break; 
                    }

                    // Handle Standard Text Token Stream
                    var openAIChunk = new
                    {
                        id = chunkId,
                        @object = "chat.completion.chunk",
                        created = createdTimestamp,
                        model = modelName,
                        choices = new[]
                        {
                            new
                            {
                                index = 0,
                                delta = new { content = text },
                                finish_reason = (string?)null
                            }
                        }
                    };

                    var json = JsonSerializer.Serialize(openAIChunk, serializerOptions);
                    await writer.WriteAsync($"data: {json}\n\n");
                    await writer.FlushAsync(token);
                }

                // Finalize text response chunk if a tool call was not triggered
                if (!toolCallTriggered && !token.IsCancellationRequested)
                {
                    var finalChunk = new
                    {
                        id = chunkId,
                        @object = "chat.completion.chunk",
                        created = createdTimestamp,
                        model = modelName,
                        choices = new[]
                        {
                            new
                            {
                                index = 0,
                                delta = new { },
                                finish_reason = "stop"
                            }
                        }
                    };
                    var finalJson = JsonSerializer.Serialize(finalChunk, serializerOptions);
                    await writer.WriteAsync($"data: {finalJson}\n\n");
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