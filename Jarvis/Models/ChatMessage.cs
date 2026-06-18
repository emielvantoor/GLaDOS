using System.Text.Json.Serialization;

namespace Jarvis.Models;

public class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user"; // system, user, assistant

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}