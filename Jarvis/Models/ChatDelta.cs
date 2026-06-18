using System.Text.Json.Serialization;

namespace Jarvis.Models;

public class ChatDelta
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}