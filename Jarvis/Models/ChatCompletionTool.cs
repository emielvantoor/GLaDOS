using System.Text.Json.Serialization;

namespace Jarvis.Models;

public class ChatCompletionTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ChatCompletionFunction Function { get; set; } = new();
}