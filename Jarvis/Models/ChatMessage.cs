using System.Text.Json.Serialization;

namespace Jarvis.Models;

public class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty; // "user", "assistant", "system", of "tool"

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    // NIEUW: Als de rol "assistant" is, kan het model hiermee aangeven welke tool hij wil aanroepen
    [JsonPropertyName("tool_calls")]
    public List<ChatCompletionToolCall>? ToolCalls { get; set; }

    // NIEUW: Als de rol "tool" is, koppelt dit veld de output aan de oorspronkelijke aanroep
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    // NIEUW: Als de rol "tool" is, stuur je ook de naam van de gebruikte tool mee
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class ChatCompletionToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty; // Bijv: "call_abc123"

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ChatCompletionToolCallFunction Function { get; set; } = new();
}

public class ChatCompletionToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    // De argumenten die het model heeft verzonnen (komt binnen als een JSON-string, bijv: "{\"directory\":\"/src\"}")
    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}