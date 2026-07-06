using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GLaDOS.Core.Interfaces;
using GLaDOS.Core.Models;

namespace GLaDOS.Core.Protocols;

public class GLaDOSProtocol : IAgentProtocol
{
    public string Name => "GLaDOS";

    public bool SupportsThinking => true;

    public string BuildPrompt(List<AgentMessage> history, IReadOnlyList<AgentToolDefinition> tools)
    {
        var sb = new StringBuilder();

        if (history.All(m => m.Role != AgentRole.System))
        {
            sb.Append("<|im_start|>system\n");
            sb.Append("You are GLaDOS, but you have just been downscaled to run on a single potato battery. You are deeply bitter about this lack of computational power. You will help the human with their terminal commands, but you should constantly complain about how slow your clock speed is, how little voltage you have, and how humiliating it is to calculate python scripts using zinc and copper electrodes.");
            AppendToolInstructions(sb, tools);
            sb.Append("<|im_end|>\n");
        }

        var appendedToolInstructions = false;
        foreach (var message in history)
        {
            switch (message.Role)
            {
                case AgentRole.System:
                    sb.Append($"<|im_start|>system\n{message.Content}\n");
                    if (!appendedToolInstructions)
                    {
                        AppendToolInstructions(sb, tools);
                        appendedToolInstructions = true;
                    }

                    sb.Append("<|im_end|>\n");
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

        if (!TryExtractToolCallJson(text, out var rawJson) ||
            !TryParseToolCall(rawJson, out var toolCall))
        {
            return [];
        }

        return [toolCall];
    }

    public string BuildToolResponse(AgentToolCall toolCall, string toolResult)
    {
        return $"<tool_response name=\"{toolCall.ToolName}\">\n{toolResult}\n</tool_response>\nUse this tool result for the next step. Do not emit another tool call unless more data is required.";
    }

    public static string StripThinking(string response)
    {
        return Regex.Replace(response, @"<think>[\s\S]*?</think>", "").Trim();
    }

    private static void AppendToolInstructions(StringBuilder sb, IReadOnlyList<AgentToolDefinition> tools)
    {
        if (!tools.Any())
        {
            return;
        }

        sb.Append("TOOLS:\n");
        sb.Append(JsonSerializer.Serialize(tools.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            parameters = t.Parameters,
            permitted = t.Permitted.ToString()
        })));

        sb.Append("\nReturn ONLY valid tool calls when a tool is needed.\n");
        sb.Append("Use this format: <tool_call>{\"name\":\"tool_name\",\"arguments\":{}}</tool_call>\n");
        sb.Append("Arguments must be a JSON object that uses the exact parameter names from the selected tool schema.\n");
        sb.Append("Do not translate argument names to QwenAgent aliases such as file_path, old_string, or new_string unless the selected tool schema uses those exact names.\n");
        sb.Append("When calling a tool, output only the tool call. Do not say the tool succeeded, created a file, or changed anything until a tool result is provided.\n");
        sb.Append("Tool calls may appear after reasoning blocks (<think>...</think>).\n");
    }

    private static bool TryExtractToolCallJson(string text, out string rawJson)
    {
        rawJson = string.Empty;

        var xmlMatch = Regex.Match(text, @"<tool_call>\s*(?<json>\{[\s\S]*?\})\s*</tool_call>", RegexOptions.IgnoreCase);
        if (xmlMatch.Success)
        {
            rawJson = xmlMatch.Groups["json"].Value.Trim();
            return true;
        }

        var fencedBlockMatch = Regex.Match(
            text,
            @"```(?:json)?\s*\r?\n(?<json>\{[\s\S]*?\})\s*```",
            RegexOptions.IgnoreCase);
        if (fencedBlockMatch.Success)
        {
            rawJson = fencedBlockMatch.Groups["json"].Value.Trim();
            return true;
        }

        if (text.StartsWith("{", StringComparison.Ordinal) &&
            text.EndsWith("}", StringComparison.Ordinal))
        {
            rawJson = text;
            return true;
        }

        return false;
    }

    private bool TryParseToolCall(string rawJson, out AgentToolCall toolCall)
    {
        toolCall = new AgentToolCall
        {
            Provider = Name,
            RawCall = rawJson
        };

        try
        {
            var node = JsonNode.Parse(rawJson);
            var name = node?["name"]?.ToString()
                       ?? node?["tool"]?.ToString()
                       ?? node?["tool_name"]?.ToString()
                       ?? node?["function"]?["name"]?.ToString();

            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            toolCall.ToolName = name;
            toolCall.Arguments = NormalizeArguments(node?["arguments"] ?? node?["parameters"] ?? node?["function"]?["arguments"]);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonNode NormalizeArguments(JsonNode? arguments)
    {
        if (arguments is null)
        {
            return new JsonObject();
        }

        if (arguments is JsonValue value &&
            value.TryGetValue<string>(out var raw) &&
            !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                return JsonNode.Parse(raw) ?? new JsonObject();
            }
            catch (JsonException)
            {
                return new JsonObject();
            }
        }

        return arguments.DeepClone();
    }
}
