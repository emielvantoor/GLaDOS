using System.Text.Json.Serialization;

namespace GLaDOS.Models;

public class FimCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = $"fimcmpl-{Guid.NewGuid()}";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "text_completion";

    [JsonPropertyName("created")]
    public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("model")]
    public string Model { get; set; } = "local-model";

    [JsonPropertyName("choices")]
    public List<FimCompletionChoice> Choices { get; set; } = new();
}

public class FimCompletionChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("finish_reason")]
    public string FinishReason { get; set; } = "stop";
}
