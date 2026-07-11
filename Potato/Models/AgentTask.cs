using System.Text.Json.Serialization;

namespace Potato.Models;

public sealed record AgentTask
{
    [JsonPropertyName("Step")]
    public int Step { get; init; }

    [JsonPropertyName("Action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("Argument")]
    public string Argument { get; init; } = string.Empty;

    [JsonPropertyName("Reason")]
    public string Reason { get; init; } = string.Empty;

    public double GetTargetTemperature()
    {
        return Action.Trim().ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal) switch
        {
            "read" or "apply-patch" or "review_code" => 0.0,
            "write-report" => 0.7,
            _ => 0.0
        };
    }
}