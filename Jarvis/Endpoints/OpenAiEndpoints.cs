using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jarvis.Core.Agents;
using Jarvis.Core.Models;
using Jarvis.Core.Services;
using Jarvis.Extensions;
using Jarvis.Models;

namespace Jarvis.Endpoints;

public static class OpenAiEndpoints
{
    public static void MapOpenAiEndpoints(this IEndpointRouteBuilder app)
    {
        // Maak een route groep aan voor v1
        var v1Group = app.MapGroup("/v1");

        v1Group.MapGet("/models/{model}", GetSpecificModel);
        v1Group.MapGet("/models", GetModels);

        v1Group.MapPost("/chat/completions", HandleChatCompletions);
    }

    // private static async Task<IResult> HandleChatCompletions(IModelManager modelManager, ChatCompletionRequest request,
    //     HttpContext context, CancellationToken cancellationToken)
    // {
    //     if (request?.Messages == null || !request.Messages.Any())
    //     {
    //         return Results.BadRequest("Invalide request of lege berichtenlijst.");
    //     }
    //
    //     LanguageModel model;
    //     try
    //     {
    //         model = await modelManager.GetAndInitializeModel(request.Model);
    //     }
    //     catch (Exception ex)
    //     {
    //         return Results.Problem($"Fout bij laden van model: {ex.Message}");
    //     }
    //
    //     // 1. Bouw de chatgeschiedenis om naar de Phi Prompt Template
    //     var sb = new StringBuilder();
    //     foreach (var msg in request.Messages)
    //     {
    //         var role = msg.Role.ToLower() switch
    //         {
    //             "system" => "system",
    //             "user" => "user",
    //             "assistant" => "assistant",
    //             _ => "user"
    //         };
    //         sb.Append($"<|{role}|>\n{msg.Content}<|end|>\n");
    //     }
    //
    //     sb.Append("<|assistant|>\n");
    //     string formattedPrompt = sb.ToString();
    //
    //     // Lijst met tokens die we NOOIT willen doorsturen naar de client
    //     var stopTokens = new[] { "<|end|>", "<|user|>", "<|assistant|>", "<|system|>" };
    //
    //
    //     // 2. Afhandeling op basis van Streaming of Non-Streaming
    //     if (request.Stream)
    //     {
    //         context.Response.ContentType = "text/event-stream";
    //         context.Response.Headers.CacheControl = "no-cache";
    //
    //         var streamBuffer = new StringBuilder();
    //
    //         await foreach (var (text, percent) in model.GenerateResponseAsync(formattedPrompt, cancellationToken))
    //         {
    //             if (string.IsNullOrEmpty(text))
    //                 continue;
    //
    //             if (cancellationToken.IsCancellationRequested)
    //                 break;
    //
    //             streamBuffer.Append(text);
    //             string currentFullText = streamBuffer.ToString();
    //
    //             // 1. Harde stop: als de complete tag in de buffer zit, kappen we direct af
    //             if (stopTokens.Any(token => currentFullText.Contains(token)))
    //                 break;
    //
    //             // 2. Slimme buffer-check: Alleen skippen als er ECHT een ChatML tag start (dus met <| en niet een los woord)
    //             // Dit voorkomt dat normale woorden zoals "user" of "user|" de stream ophouden
    //             if (text.Contains("<|") || (currentFullText.Contains("<") && !currentFullText.Contains(">")))
    //             {
    //                 // We wachten even tot de tag compleet is om te zien of het een stoptag is
    //                 continue;
    //             }
    //
    //             // Haal de tekst op die we veilig kunnen doorsturen
    //             // Als de buffer veilig is, legen we hem naar de client
    //             var textToSend = streamBuffer.ToString();
    //             streamBuffer.Clear();
    //
    //             var chunkResponse = new ChatCompletionChunk { Model = request.Model };
    //             chunkResponse.Choices.Add(new ChatChunkChoice { Delta = new ChatDelta { Content = textToSend } });
    //
    //             var json = JsonSerializer.Serialize(chunkResponse);
    //             await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
    //             await context.Response.Body.FlushAsync(cancellationToken);
    //         }
    //
    //         await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
    //         await context.Response.Body.FlushAsync(cancellationToken);
    //
    //         return Results.Empty;
    //     }
    //     else
    //     {
    //         var response = new ChatCompletionResponse { Model = request.Model };
    //         var fullContent = new StringBuilder();
    //
    //         await foreach (var (text, percent) in model.GenerateResponseAsync(formattedPrompt, cancellationToken))
    //         {
    //             if (string.IsNullOrEmpty(text))
    //                 continue;
    //
    //             if (cancellationToken.IsCancellationRequested)
    //                 break;
    //
    //             // EXTRA CHECK: Als het token een stop-tag bevat, negeren en stoppen
    //             if (stopTokens.Any(token => text.Contains(token)))
    //                 break;
    //
    //             fullContent.Append(text);
    //         }
    //
    //         response.Choices.Add(new ChatChoice
    //             { Message = new ChatMessage { Role = "assistant", Content = fullContent.ToString() } });
    //
    //         return Results.Ok(response);
    //     }
    // }

    // Maak een statische semaphore aan die maximaal 1 request tegelijk doorlaat
    private static readonly SemaphoreSlim _llmLock = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Handles chat completions by processing the request, initializing the model, and streaming the response.
    /// </summary>
    /// <param name="modelManager">The model manager to use for retrieving and initializing the model.</param>
    /// <param name="request">The chat completion request containing the messages.</param>
    /// <param name="agent">The agent to handle the interaction.</param>
    /// <param name="context">The HTTP context for the request.</param>
    /// <returns>An asynchronous result representing the chat completion response.</returns>
private static async Task<IResult> HandleChatCompletions(IModelManager modelManager,
    JarvisAgent agent, HttpContext context)
{
    // Dit is het échte live-signaal van Rider
    var token = context.RequestAborted;

    try
    {
        await _llmLock.WaitAsync(token);
    }
    catch (OperationCanceledException)
    {
        return Results.BadRequest("Operation cancelled");
    }

    Console.WriteLine("Handling Chat Completions");

    // 1. Lees het request handmatig als een stream uit de HTTP body
    ChatCompletionRequest? request = null;
    try
    {
        request = await JsonSerializer.DeserializeAsync<ChatCompletionRequest>(
            context.Request.Body,
            cancellationToken: token);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Fout bij het lezen van Rider request stream: {ex.Message}");
        _llmLock.Release(); // Vergeet de lock niet vrij te geven bij een vroege crash
        return Results.BadRequest("Invalide JSON of stream afgebroken.");
    }

    if (request?.Messages == null || !request.Messages.Any())
    {
        _llmLock.Release();
        return Results.BadRequest("Invalide request of lege berichtenlijst.");
    }

    LanguageModel model;
    try
    {
        model = await modelManager.GetAndInitializeModel(request.Model);
    }
    catch (Exception ex)
    {
        _llmLock.Release();
        return Results.Problem($"Fout bij laden van model: {ex.Message}");
    }

    // Zet de tools om (omgerekend naar null-safe lijsten voor .NET 8/9 expressies)
    var domainMessages = request.Messages.Select(message => message.ToDomainModel()).ToList();
    var domainTools = request.Tools?.Select(tool => tool.ToDomainModel()).ToList() ?? new();

    // De agent handelt nu autonoom de complete interactie af!
    var agentResultStream = agent.RunAsync(model, domainMessages, domainTools, token);

    // Gebruik de juiste encoder om HTML/Markdown escaping te minimaliseren
    var serializerOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Genereer één unieke ID voor de gehele sessie/request
    string chunkId = $"chatcmpl-{Guid.NewGuid()}";
    long createdTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    string modelName = request.Model ?? "local-model";

    return Results.Stream(async stream =>
    {
        try
        {
            await using var writer = new StreamWriter(stream) { NewLine = "\n" };
            bool toolCallTriggered = false;

            await foreach (var text in agentResultStream)
            {
                if (token.IsCancellationRequested) break;

                // INTERCEPTIE: Check of de agent een externe tool call van Rider heeft gegenereerd
                if (!string.IsNullOrEmpty(text) && text.StartsWith("__TOOL_CALL__:"))
                {
                    toolCallTriggered = true;
                    
                    // Format parseren: "__TOOL_CALL__:tool_name|{arguments}"
                    var payload = text["__TOOL_CALL__:".Length..];
                    var separatorIndex = payload.IndexOf('|');
                    
                    string toolName = separatorIndex >= 0 ? payload[..separatorIndex] : payload;
                    string toolArgs = separatorIndex >= 0 ? payload[(separatorIndex + 1)..] : "{}";

                    // Bouw de officiële OpenAI Tool Call Chunk voor de IDE
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
                                            id = $"call_{Guid.NewGuid():n}", // Genereer een schone, unieke hex-string
                                            type = "function",
                                            function = new
                                            {
                                                name = toolName,
                                                arguments = toolArgs
                                            }
                                        }
                                    }
                                },
                                finish_reason = "tool_calls" // Dit triggert de executie binnen Rider
                            }
                        }
                    };

                    var jsonTool = JsonSerializer.Serialize(toolCallChunk, serializerOptions);
                    await writer.WriteAsync($"data: {jsonTool}\n\n");
                    await writer.FlushAsync(token);
                    
                    break; // Breek de loop direct af; Rider krijgt nu de leiding over de workflow!
                }

                // Normale text-generation chunks streamen
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

            // Alleen de reguliere "stop" chunks versturen als er géén tool call is afgehandeld
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

            // Sluit de SSE stream netjes af volgens protocol
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(token);
        }
        finally
        {
            // Lock en resources vrijgeven
            _llmLock.Release();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }, "text/event-stream");
}

    private static IResult GetModels(IModelManager modelManager)
    {
        var modelsList = new ModelListResponse();

        var models = modelManager.GetAvailableModels();
        modelsList.Data.AddRange(models.Select(m => m.ToDto()));

        return Results.Ok(modelsList);
    }

    private static IResult GetSpecificModel(string model)
    {
        // Als IntelliJ specifiek naar gpt-4o (of iets anders) vraagt, geven we direct groen licht
        return Results.Ok(new ModelData { Id = model, OwnedBy = "openai" });
    }
}