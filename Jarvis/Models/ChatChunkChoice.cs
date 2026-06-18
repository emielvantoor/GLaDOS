using System.Text.Json.Serialization;

namespace Jarvis.Models;

public class ChatChunkChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; } = 0;

    [JsonPropertyName("delta")]
    public ChatDelta Delta { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; } = null;
}