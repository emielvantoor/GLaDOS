using System.Text.Json.Serialization;

namespace GLaDOS.Models;

public class ModelPermission
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = $"modelperm-{Guid.NewGuid()}";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "model_permission";

    [JsonPropertyName("created")]
    public long Created { get; set; } = 1717830000;

    [JsonPropertyName("allow_create_engine")]
    public bool AllowCreateEngine { get; set; } = true;

    [JsonPropertyName("allow_sampling")]
    public bool AllowSampling { get; set; } = true;

    [JsonPropertyName("allow_logprobs")]
    public bool AllowLogprobs { get; set; } = true;

    [JsonPropertyName("allow_search_indices")]
    public bool AllowSearchIndices { get; set; } = true;

    [JsonPropertyName("allow_view")]
    public bool AllowView { get; set; } = true;

    [JsonPropertyName("allow_fine_tuning")]
    public bool AllowFineTuning { get; set; } = false;

    [JsonPropertyName("organization")]
    public string Organization { get; set; } = "*";

    [JsonPropertyName("group")]
    public string? Group { get; set; } = null;

    [JsonPropertyName("is_blocking")]
    public bool IsBlocking { get; set; } = false;
}