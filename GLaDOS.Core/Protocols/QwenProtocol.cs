using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GLaDOS.Core.Interfaces;
using GLaDOS.Core.Models;

namespace GLaDOS.Core.Protocols;

public class QwenProtocol : IAgentProtocol
{
    public virtual string Name => "Qwen";

    public virtual bool SupportsThinking => true;

    public string BuildPrompt(List<AgentMessage> history, IReadOnlyList<AgentToolDefinition> tools)
    {
        return QwenPromptFormatter.BuildPrompt(history, tools);
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

    public virtual string BuildToolResponse(AgentToolCall toolCall, string toolResult)
    {
        return $"<tool_response name=\"{toolCall.ToolName}\">\n{toolResult}\n</tool_response>\nAnswer the user now using this tool result. Do not emit another tool call unless more data is required.";
    }

    public string CleanResponse(string response)
    {
        return StripThinking(response);
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

        var openBracketMatch = Regex.Match(text, @"\[tool_call:\s*(?<content>[\s\S]*)", RegexOptions.IgnoreCase);
        if (openBracketMatch.Success)
        {
            toolContent = openBracketMatch.Groups["content"].Value.Trim();
            return true;
        }

        var namedJsonMatch = Regex.Match(
            text,
            @"^\s*(?<name>[A-Za-z_]\w*)\s+(?<args>\{[\s\S]*\})\s*$");
        if (namedJsonMatch.Success)
        {
            toolContent = namedJsonMatch.Value.Trim();
            return true;
        }

        var functionCallMatch = Regex.Match(
            text,
            @"(?<![\w.])(?<name>[A-Za-z_]\w*)\s*\((?<args>[^\r\n]*?)\)\s*$");
        if (functionCallMatch.Success)
        {
            toolContent = functionCallMatch.Value.Trim();
            return true;
        }

        var fencedBlockMatch = Regex.Match(
            text,
            @"```(?<language>[A-Za-z0-9_-]*)\s*\r?\n(?<content>[\s\S]*?)```",
            RegexOptions.IgnoreCase);
        if (fencedBlockMatch.Success)
        {
            var language = fencedBlockMatch.Groups["language"].Value;
            var fencedContent = fencedBlockMatch.Groups["content"].Value.Trim();
            if ((string.IsNullOrEmpty(language) || language.Equals("json", StringComparison.OrdinalIgnoreCase)) &&
                fencedContent.StartsWith("{", StringComparison.Ordinal) &&
                fencedContent.EndsWith("}", StringComparison.Ordinal) &&
                ContainsToolIdentifier(fencedContent))
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
        return Regex.IsMatch(
            text,
            @"[""'](?:name|tool|tool_name|function)[""']\s*:",
            RegexOptions.IgnoreCase);
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

        if (TryParseQwenCliStyleToolCall(raw, toolCall))
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

    private static bool TryParseQwenCliStyleToolCall(string raw, AgentToolCall toolCall)
    {
        var pathMatch = Regex.Match(
            raw,
            @"^(?<name>[A-Za-z_]\w*)\s+for\s+path\s+['""](?<path>[^'""]+)['""](?<tail>[\s\S]*)$",
            RegexOptions.IgnoreCase);

        if (!pathMatch.Success)
        {
            return false;
        }

        toolCall.ToolName = pathMatch.Groups["name"].Value;
        var arguments = new JsonObject
        {
            ["file_path"] = pathMatch.Groups["path"].Value
        };

        var tail = pathMatch.Groups["tail"].Value;
        var contentMatch = Regex.Match(
            tail,
            @"\bwith\s+content\s*:\s*(?<content>[\s\S]*)",
            RegexOptions.IgnoreCase);

        if (contentMatch.Success)
        {
            arguments["content"] = NormalizeNumberedContent(contentMatch.Groups["content"].Value);
        }

        toolCall.Arguments = arguments;
        return true;
    }

    private static string NormalizeNumberedContent(string rawContent)
    {
        var lines = rawContent.Replace("\r\n", "\n").Split('\n');
        var contentLines = new List<string>();
        var sawNumberedLine = false;

        foreach (var line in lines)
        {
            var numberedLine = Regex.Match(line, @"^\s*\d+\s?(?<content>.*)$");
            if (numberedLine.Success)
            {
                sawNumberedLine = true;
                contentLines.Add(numberedLine.Groups["content"].Value.TrimEnd());
                continue;
            }

            if (sawNumberedLine)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                break;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                contentLines.Add(line.Trim());
            }
        }

        return string.Join('\n', contentLines).TrimEnd();
    }

    private static bool TryParseFunctionStyleToolCall(string raw, AgentToolCall toolCall)
    {
        var functionMatch = Regex.Match(
            raw.Trim(),
            @"^(?<name>[A-Za-z_]\w*)\s*\((?<args>[^\r\n]*?)\)$");

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
