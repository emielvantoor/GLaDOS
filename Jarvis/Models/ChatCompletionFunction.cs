using System.Text.Json.Serialization;

namespace Jarvis.Models;

public class ChatCompletionFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    // Parameters worden door OpenAI/Rider meegestuurd als een complex JSON-schema object
    [JsonPropertyName("parameters")]
    public System.Text.Json.Nodes.JsonObject? Parameters { get; set; }
}