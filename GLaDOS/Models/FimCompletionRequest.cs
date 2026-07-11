using System.Text.Json;
using System.Text.Json.Serialization;

namespace GLaDOS.Models;

public class FimCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "local-model";

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("prefix")]
    public string? Prefix { get; set; }

    [JsonPropertyName("suffix")]
    public string? Suffix { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokenLength { get; set; }

    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }

    [JsonPropertyName("stop")]
    public JsonElement? Stop { get; set; }
}
