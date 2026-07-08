using System.Text.Json.Serialization;

internal sealed record AgentTask
{
    [JsonPropertyName("step")]
    public int Step { get; init; }

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("argument")]
    public string Argument { get; init; } = string.Empty;

    public double GetTargetTemperature()
    {
        return Action.Trim().ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal) switch
        {
            "read" or "patch" => 0.0,
            "write-summary" or "write-documentation" => 0.4,
            "explain-to-user" => 0.7,
            _ => 0.0
        };
    }
}

internal sealed record SearchReplacePatch
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("search")]
    public string Search { get; init; } = string.Empty;

    [JsonPropertyName("replace")]
    public string Replace { get; init; } = string.Empty;
}

internal sealed record CreatedFile
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}
