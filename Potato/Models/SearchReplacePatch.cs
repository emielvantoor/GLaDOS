using System.Text.Json.Serialization;

namespace Potato;

public sealed record SearchReplacePatch
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("search")]
    public string Search { get; init; } = string.Empty;

    [JsonPropertyName("replace")]
    public string Replace { get; init; } = string.Empty;
}