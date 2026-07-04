using System.Text.Json.Serialization;
using GLaDOS.Core.Tools;

namespace GLaDOS.Models;

public class ChatCompletionTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ChatCompletionFunction Function { get; set; } = new();

    [JsonPropertyName("permitted")]
    public ToolPermission Permitted { get; set; } = ToolPermission.User;
}
