using System.Text.Json.Nodes;

namespace GLaDOS.Core.Models;

public class AgentToolCall
{
    public string Provider { get; set; } = string.Empty;

    public string ToolName { get; set; } = string.Empty;

    public JsonNode? Arguments { get; set; }

    public string RawCall { get; set; } = string.Empty;
}
