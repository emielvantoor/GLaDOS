using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jarvis.Core.Interfaces;
using Jarvis.Core.Models;

namespace Jarvis.Core.Protocols;

public class QwenProtocol : IAgentProtocol
{
    public virtual string Name => "Qwen";

    public virtual bool SupportsThinking => true;

    public string BuildPrompt(List<AgentMessage> history, IReadOnlyList<AgentToolDefinition> tools)
    {
        var sb = new StringBuilder();

        if (history.All(m => m.Role != AgentRole.System))
        {
            sb.Append("<|im_start|>system\n");
            sb.Append("You are Jarvis, an autonomous AI assistant.\n");
            sb.Append("When a tool result is provided, use that result to answer the user in plain text. Do not call the same tool again unless the user asks for another lookup.\n");

            if (tools.Any())
            {
                sb.Append("TOOLS:\n");
                sb.Append(JsonSerializer.Serialize(tools.Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.Parameters
                })));

                sb.Append("\nReturn ONLY valid tool calls when a tool is needed.\n");
                sb.Append("Use this format: <tool_call>{\"name\":\"tool_name\",\"arguments\":{}}</tool_call>\n");
                sb.Append("Tool calls may appear after reasoning blocks (<think>...</think>).\n");
            }

            sb.Append("<|im_end|>\n");
        }

        foreach (var message in history)
        {
            switch (message.Role)
            {
                case AgentRole.System:
                    sb.Append($"<|im_start|>system\n{message.Content}<|im_end|>\n");
                    break;

                case AgentRole.User:
                    sb.Append($"<|im_start|>user\n{message.Content}<|im_end|>\n");
                    break;

                case AgentRole.Assistant:
                    if (!string.IsNullOrEmpty(message.ToolCallName))
                    {
                        sb.Append($"<|im_start|>assistant\n<tool_call>{{\"name\":\"{message.ToolCallName}\",\"arguments\":{message.ToolCallArgs ?? "{}"}}}</tool_call><|im_end|>\n");
                    }
                    else
                    {
                        sb.Append($"<|im_start|>assistant\n{message.Content}<|im_end|>\n");
                    }

                    break;

                case AgentRole.Tool:
                    var toolCall = new AgentToolCall
                    {
                        Provider = Name,
                        ToolName = message.ToolCallName ?? string.Empty,
                        RawCall = message.ToolCallArgs ?? string.Empty
                    };
                    sb.Append($"<|im_start|>user\n{BuildToolResponse(toolCall, message.Content)}<|im_end|>\n");
                    break;
            }
        }

        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    public IEnumerable<AgentToolCall> ParseResponse(string response)
    {
        var text = StripThinking(response).Trim();

        if (!DetectToolCall(text, out var rawToolContent))
        {
            return [];
        }

        var normalized = NormalizeToolCall(rawToolContent);
        if (!TryParseToolCall(normalized, out var toolCall))
        {
            return [];
        }

        return [toolCall];
    }

    public string BuildToolResponse(AgentToolCall toolCall, string toolResult)
    {
        return $"<tool_response name=\"{toolCall.ToolName}\">\n{toolResult}\n</tool_response>\nAnswer the user now using this tool result. Do not emit another tool call unless more data is required.";
    }

    public string StripThinking(string response)
    {
        return Regex.Replace(response, @"<think>[\s\S]*?</think>", "").Trim();
    }

    private static bool DetectToolCall(string text, out string toolContent)
    {
        toolContent = string.Empty;

        var xmlMatch = Regex.Match(text, @"<tool_call>([\s\S]*?)</tool_call>");
        if (xmlMatch.Success)
        {
            toolContent = xmlMatch.Groups[1].Value.Trim();
            return true;
        }

        var bracketMatch = Regex.Match(text, @"\[tool_call:([\s\S]*?)\]");
        if (bracketMatch.Success)
        {
            toolContent = bracketMatch.Groups[1].Value.Trim();
            return true;
        }

        var functionCallMatch = Regex.Match(
            text,
            @"(?<!\w)(?<name>[A-Za-z_][\w.-]*)\s*\((?<args>[\s\S]*?)\)\s*$");
        if (functionCallMatch.Success)
        {
            toolContent = functionCallMatch.Value.Trim();
            return true;
        }

        var fencedJsonMatch = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
        if (fencedJsonMatch.Success)
        {
            var fencedContent = fencedJsonMatch.Groups[1].Value.Trim();
            if (ContainsToolIdentifier(fencedContent))
            {
                toolContent = fencedContent;
                return true;
            }
        }

        if (text.StartsWith("{", StringComparison.Ordinal) &&
            text.EndsWith("}", StringComparison.Ordinal) &&
            ContainsToolIdentifier(text))
        {
            toolContent = text;
            return true;
        }

        return false;
    }

    private static bool ContainsToolIdentifier(string text)
    {
        return text.Contains("name", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("tool", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeToolCall(string raw)
    {
        return raw
            .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
            .Replace("```", "", StringComparison.Ordinal)
            .Trim();
    }

    private bool TryParseToolCall(string raw, out AgentToolCall toolCall)
    {
        toolCall = new AgentToolCall
        {
            Provider = Name,
            RawCall = raw
        };

        if (TryParseJsonToolCall(raw, toolCall))
        {
            return true;
        }

        if (TryParseLooseToolCall(raw, toolCall))
        {
            return true;
        }

        if (TryParseFunctionStyleToolCall(raw, toolCall))
        {
            return true;
        }

        var splitIndex = raw.IndexOf(' ');
        var toolName = splitIndex < 0 ? raw.Trim() : raw[..splitIndex].Trim();
        var arguments = splitIndex < 0 ? "{}" : raw[(splitIndex + 1)..].Trim();

        if (string.IsNullOrEmpty(toolName))
        {
            return false;
        }

        toolCall.ToolName = toolName;
        toolCall.Arguments = TryParseJsonNode(arguments) ?? new JsonObject { ["value"] = arguments };
        return true;
    }

    private static bool TryParseFunctionStyleToolCall(string raw, AgentToolCall toolCall)
    {
        var functionMatch = Regex.Match(
            raw.Trim(),
            @"^(?<name>[A-Za-z_][\w.-]*)\s*\((?<args>[\s\S]*?)\)$");

        if (!functionMatch.Success)
        {
            return false;
        }

        toolCall.ToolName = functionMatch.Groups["name"].Value;

        var rawArgs = functionMatch.Groups["args"].Value.Trim();
        if (string.IsNullOrEmpty(rawArgs))
        {
            toolCall.Arguments = new JsonObject();
            return true;
        }

        toolCall.Arguments = TryParseJsonNode(rawArgs) ?? ParseLooseArguments($"arguments: {{{rawArgs}}}");
        return true;
    }

    private static bool TryParseLooseToolCall(string raw, AgentToolCall toolCall)
    {
        var nameMatch = Regex.Match(
            raw,
            @"(?<!\w)(?:name|tool)\s*[:=]\s*[""']?(?<name>[A-Za-z_][\w.-]*)[""']?",
            RegexOptions.IgnoreCase);

        if (!nameMatch.Success)
        {
            return false;
        }

        toolCall.ToolName = nameMatch.Groups["name"].Value;
        toolCall.Arguments = ParseLooseArguments(raw);
        return true;
    }

    private static JsonNode ParseLooseArguments(string raw)
    {
        var argumentsMatch = Regex.Match(
            raw,
            @"(?<!\w)(?:arguments|parameters)\s*[:=]\s*(?<args>\{[\s\S]*?\}|\[[\s\S]*?\]|""(?:\\.|[^""])*""|'(?:\\.|[^'])*')",
            RegexOptions.IgnoreCase);

        if (!argumentsMatch.Success)
        {
            return new JsonObject();
        }

        var arguments = argumentsMatch.Groups["args"].Value.Trim();
        if ((arguments.StartsWith("\"", StringComparison.Ordinal) && arguments.EndsWith("\"", StringComparison.Ordinal)) ||
            (arguments.StartsWith("'", StringComparison.Ordinal) && arguments.EndsWith("'", StringComparison.Ordinal)))
        {
            arguments = arguments[1..^1];
        }

        return TryParseJsonNode(arguments) ?? new JsonObject { ["value"] = arguments };
    }

    private static bool TryParseJsonToolCall(string raw, AgentToolCall toolCall)
    {
        try
        {
            var node = JsonNode.Parse(raw);
            if (node == null)
            {
                return false;
            }

            var name = node["name"]?.ToString()
                ?? node["tool"]?.ToString()
                ?? node["tool_name"]?.ToString()
                ?? node["function"]?["name"]?.ToString()
                ?? string.Empty;

            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            toolCall.ToolName = name;
            toolCall.Arguments = NormalizeArguments(node["arguments"] ?? node["parameters"]);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonNode? TryParseJsonNode(string raw)
    {
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode NormalizeArguments(JsonNode? arguments)
    {
        if (arguments == null)
        {
            return new JsonObject();
        }

        if (arguments is JsonValue value &&
            value.TryGetValue<string>(out var raw) &&
            !string.IsNullOrWhiteSpace(raw))
        {
            return TryParseJsonNode(raw) ?? new JsonObject { ["value"] = raw };
        }

        return arguments.DeepClone();
    }
}
