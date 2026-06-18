using System.Text.Json.Serialization;

namespace Jarvis.Models;

public class ChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = $"chatcmpl-{Guid.NewGuid()}";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion";

    [JsonPropertyName("created")]
    public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("model")]
    public string Model { get; set; } = "phi-silica";

    [JsonPropertyName("choices")]
    public List<ChatChoice> Choices { get; set; } = new();
}