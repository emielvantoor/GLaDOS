using System.Text.Json.Serialization;

namespace Potato.Models;

internal sealed record CreatedFile
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}