using System.Text.Json.Serialization;

internal sealed class ShellCommandPlan
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 60;
}
