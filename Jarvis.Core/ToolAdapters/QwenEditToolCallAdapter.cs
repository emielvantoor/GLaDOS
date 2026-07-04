using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jarvis.Core.Interfaces;
using Jarvis.Core.Models;

namespace Jarvis.Core.ToolAdapters;

public class QwenEditToolCallAdapter : ToolCallAdapter
{
    public override bool CanAdapt(AgentToolCall toolCall, ToolCallAdapterContext context)
    {
        return string.Equals(toolCall.Provider, "Qwen", StringComparison.Ordinal) &&
               toolCall.Arguments is JsonObject &&
               RequiresProperties(toolCall, context, "old_string", "new_string");
    }

    public override void Adapt(AgentToolCall toolCall, ToolCallAdapterContext context)
    {
        if (toolCall.Arguments is not JsonObject arguments)
        {
            return;
        }

        if (!arguments.ContainsKey("new_string") &&
            arguments.TryGetPropertyValue("content", out var contentNode))
        {
            arguments["new_string"] = ToolCallJson.NormalizeStringArgument(contentNode);
            arguments.Remove("content");
        }

        if (arguments.TryGetPropertyValue("new_string", out var newStringNode))
        {
            arguments["new_string"] = ToolCallJson.NormalizeStringArgument(newStringNode);
        }

        if (arguments.TryGetPropertyValue("old_string", out var oldStringNode))
        {
            arguments["old_string"] = ToolCallJson.NormalizeStringArgument(oldStringNode);
        }

        if (!arguments.ContainsKey("new_string"))
        {
            return;
        }

        var filePath = arguments["file_path"]?.ToString();
        var latestReadContent = FindLastReadFileContent(context.ChatHistory, filePath);
        if (string.IsNullOrEmpty(latestReadContent))
        {
            RewriteEditAsReadFile(toolCall, context.ToolDefinitions, filePath);
            return;
        }

        var suppliedOldString = arguments["old_string"]?.ToString();
        if (string.IsNullOrEmpty(suppliedOldString) ||
            !latestReadContent.Contains(suppliedOldString, StringComparison.Ordinal) &&
            LooksLikeFullFileReplacement(arguments["new_string"]?.ToString(), latestReadContent, filePath))
        {
            arguments["old_string"] = latestReadContent;
        }
    }

    private static bool LooksLikeFullFileReplacement(
        string? newString,
        string latestReadContent,
        string? filePath)
    {
        if (string.IsNullOrEmpty(newString))
        {
            return false;
        }

        if (newString.Contains('\n') &&
            newString.Length >= Math.Max(20, latestReadContent.Length / 2))
        {
            return true;
        }

        return filePath?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true &&
               (newString.Contains("using ", StringComparison.Ordinal) ||
                newString.Contains("namespace ", StringComparison.Ordinal) ||
                newString.Contains("class ", StringComparison.Ordinal));
    }

    private static string? FindLastReadFileContent(
        IReadOnlyList<AgentMessage> chatHistory,
        string? filePath)
    {
        for (var i = chatHistory.Count - 1; i >= 0; i--)
        {
            var message = chatHistory[i];
            if (message.Role != AgentRole.Tool)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(message.ToolCallName) &&
                !message.ToolCallName.Contains("read", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var readToolCall = FindPrecedingReadToolCall(chatHistory, i, filePath);
            if (readToolCall == null &&
                string.IsNullOrEmpty(message.ToolCallName))
            {
                continue;
            }

            return ExtractReadFileContent(message.Content);
        }

        return null;
    }

    private static AgentMessage? FindPrecedingReadToolCall(
        IReadOnlyList<AgentMessage> chatHistory,
        int beforeIndex,
        string? filePath)
    {
        for (var i = beforeIndex - 1; i >= 0; i--)
        {
            var message = chatHistory[i];
            if (message.Role == AgentRole.Tool)
            {
                return null;
            }

            if (message.Role != AgentRole.Assistant ||
                string.IsNullOrEmpty(message.ToolCallName) ||
                !message.ToolCallName.Contains("read", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(filePath) &&
                !string.IsNullOrEmpty(message.ToolCallArgs) &&
                !message.ToolCallArgs.Contains(filePath, StringComparison.Ordinal))
            {
                continue;
            }

            return message;
        }

        return null;
    }

    private static string ExtractReadFileContent(string rawContent)
    {
        var content = TryExtractJsonString(rawContent, "content")
                      ?? TryExtractJsonString(rawContent, "text")
                      ?? TryExtractJsonString(rawContent, "result")
                      ?? TryExtractJsonString(rawContent, "output")
                      ?? TryExtractJsonString(rawContent, "value")
                      ?? rawContent;

        var fenced = Regex.Match(content, @"```(?:[A-Za-z0-9_-]+)?\s*\r?\n(?<content>[\s\S]*?)```");
        if (fenced.Success)
        {
            content = fenced.Groups["content"].Value;
        }

        return StripLineNumbers(content);
    }

    private static string? TryExtractJsonString(string rawContent, string propertyName)
    {
        try
        {
            var node = JsonNode.Parse(rawContent);
            var valueNode = node?[propertyName];
            if (valueNode == null)
            {
                return null;
            }

            return ToolCallJson.NormalizeStringArgument(valueNode);
        }
        catch
        {
            return null;
        }
    }

    private static string StripLineNumbers(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var nonEmptyLines = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();

        if (nonEmptyLines.Count == 0 ||
            nonEmptyLines.Any(line => !Regex.IsMatch(line, @"^\s*\d+\s*(?:[|:])?\s")))
        {
            return content;
        }

        var stripped = lines
            .Select(line => Regex.Replace(line, @"^\s*\d+\s*(?:[|:])?\s?", ""))
            .ToArray();

        return string.Join('\n', stripped);
    }

    private static void RewriteEditAsReadFile(
        AgentToolCall toolCall,
        IReadOnlyList<AgentToolDefinition> toolDefinitions,
        string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var readTool = toolDefinitions.FirstOrDefault(tool =>
            tool.Name.Contains("read", StringComparison.OrdinalIgnoreCase) &&
            tool.Parameters?["properties"]?["file_path"] != null);

        if (readTool == null)
        {
            return;
        }

        toolCall.ToolName = readTool.Name;
        toolCall.Arguments = new JsonObject
        {
            ["file_path"] = filePath
        };
    }
}
