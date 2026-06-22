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

    /// <summary>
    /// Handles chat completions by processing the request, retrieving the model, and running the agent to generate the response.
    /// </summary>
    /// <param name="modelManager">The model manager to retrieve and initialize the language model.</param>
    /// <param name="request">The chat completion request containing the messages.</param>
    /// <param name="agent">The agent to handle the complete interaction.</param>
    /// <param name="token">The cancellation token to handle asynchronous operations.</param>
    /// <returns>A task that represents the asynchronous operation. Returns a stream of chat completion chunks or an error result.</returns>
    private static async Task<IResult> HandleChatCompletions(IModelManager modelManager, ChatCompletionRequest request,
        JarvisAgent agent, CancellationToken token)
    {
        if (request?.Messages == null || !request.Messages.Any())
        {
            return Results.BadRequest("Invalide request of lege berichtenlijst.");
        }
        
        LanguageModel model;
        try
        {
            model = await modelManager.GetAndInitializeModel(request.Model);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Fout bij laden van model: {ex.Message}");
        }
        
        // De agent handelt nu autonoom de complete interactie af!
        var agentResultStream = agent.RunAsync(model, [.. request.Messages.Select(message => message.ToDomainModel())], token);
    
        // Gebruik de juiste encoder om HTML/Markdown escaping te minimaliseren
        var serializerOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

// Genereer één unieke ID voor de gehele sessie/request
        string chunkId = $"chatcmpl-{Guid.NewGuid()}";
        long createdTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string modelName = request.Model ?? "local-model"; // Zorg dat de modelnaam matcht

        return Results.Stream(async stream => {
            await using var writer = new StreamWriter(stream) { NewLine = "\n" };
    
            await foreach (var text in agentResultStream)
            {
                if (text == null) continue;

                // Bouw exact de structuur na waar Rider om vraagt
                var openAIChunk = new {
                    id = chunkId,
                    @object = "chat.completion.chunk", // 'object' is een C# keyword, dus @ gebruiken
                    created = createdTimestamp,
                    model = modelName,
                    choices = new[] { 
                        new { 
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

            // Optioneel: stuur de officiële afsluitende chunk met finish_reason "stop"
            var finalChunk = new {
                id = chunkId,
                @object = "chat.completion.chunk",
                created = createdTimestamp,
                model = modelName,
                choices = new[] { 
                    new { 
                        index = 0,
                        delta = new { },
                        finish_reason = "stop"
                    } 
                } 
            };
            var finalJson = JsonSerializer.Serialize(finalChunk, serializerOptions);
            await writer.WriteAsync($"data: {finalJson}\n\n");

            // Sluit af met de OpenAI-standaard marker
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(token);
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