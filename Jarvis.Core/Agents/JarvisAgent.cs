using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Interfaces;
using Jarvis.Core.Models;
using Jarvis.Core.Protocols;
using Jarvis.Core.Routing;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Agents;

public class JarvisAgent
{
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolRouter _toolRouter;
    private readonly IAgentProtocol _protocol;
    private readonly ILogger<JarvisAgent> _logger;

    public JarvisAgent(
        ToolRegistry toolRegistry,
        ToolRouter toolRouter,
        IAgentProtocol protocol,
        ILogger<JarvisAgent> logger)
    {
        _toolRegistry = toolRegistry;
        _toolRouter = toolRouter;
        _protocol = protocol;
        _logger = logger;
    }

    public async IAsyncEnumerable<string> RunAsync(
        LanguageModel model,
        List<AgentMessage> chatHistory,
        ChatOptions options,
        List<AgentToolDefinition>? externalTools = null, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting JarvisAgent.RunAsync with {Protocol} protocol", _protocol.Name);

        var toolDefinitions = _toolRegistry.GetDefinitions().ToList();

        if (externalTools != null)
        {
            toolDefinitions.AddRange(externalTools);
        }

        int maxIterations = 5;
        int currentIteration = 0;
        bool keepRunning = true;
        AgentToolResult? lastInternalToolResult = null;
        bool yieldedAssistantText = false;
        bool delegatedExternalTool = false;

        while (keepRunning && currentIteration < maxIterations)
        {
            currentIteration++;

            var prompt = _protocol.BuildPrompt(chatHistory, toolDefinitions);
            var response = await model.GenerateResponseAsync(prompt, options, cancellationToken);
            var toolCall = _protocol.ParseResponse(response).FirstOrDefault();

            if (toolCall == null)
            {
                var text = CleanAssistantText(response);
                if (!string.IsNullOrEmpty(text))
                {
                    yieldedAssistantText = true;
                    yield return text;
                }

                keepRunning = false;
                continue;
            }

            NormalizeToolCallArguments(toolCall, toolDefinitions, chatHistory);

            var toolResult = await _toolRouter.RouteAsync(toolCall);
            var toolArgs = toolCall.Arguments?.ToJsonString() ?? "{}";

            if (toolResult.IsExternal)
            {
                yield return $"\n[Systeem: Delegating external tool '{toolCall.ToolName}' to client...]\n";
                yield return $"__TOOL_CALL__:{toolCall.ToolName}|{toolArgs}";
                delegatedExternalTool = true;
                break;
            }

            yield return $"\n[Systeem: Executing internal tool '{toolCall.ToolName}' with arguments: {toolArgs}...]\n";

            chatHistory.Add(new AgentMessage(AgentRole.Assistant, string.Empty, toolCall.ToolName, toolArgs));
            chatHistory.Add(new AgentMessage(AgentRole.Tool, toolResult.Output, toolCall.ToolName, toolCall.RawCall));
            lastInternalToolResult = toolResult;
        }

        if (!yieldedAssistantText && !delegatedExternalTool && lastInternalToolResult != null)
        {
            yield return lastInternalToolResult.Output;
        }

        _logger.LogInformation("Finished JarvisAgent.RunAsync");
    }

    private string CleanAssistantText(string response)
    {
        return _protocol is QwenProtocol qwenProtocol
            ? qwenProtocol.StripThinking(response)
            : response.Trim();
    }

    private static void NormalizeToolCallArguments(
        AgentToolCall toolCall,
        IReadOnlyList<AgentToolDefinition> toolDefinitions,
        IReadOnlyList<AgentMessage> chatHistory)
    {
        NormalizeSingleValueArguments(toolCall, toolDefinitions);
        NormalizeEditArguments(toolCall, toolDefinitions, chatHistory);
    }

    private static void NormalizeSingleValueArguments(
        AgentToolCall toolCall,
        IReadOnlyList<AgentToolDefinition> toolDefinitions)
    {
        if (toolCall.Arguments is not JsonObject arguments ||
            arguments.Count != 1 ||
            !arguments.TryGetPropertyValue("value", out var valueNode) ||
            valueNode is not JsonValue value ||
            !value.TryGetValue<string>(out var rawValue))
        {
            return;
        }

        var toolDefinition = toolDefinitions.FirstOrDefault(tool =>
            string.Equals(tool.Name, toolCall.ToolName, StringComparison.Ordinal));
        var requiredProperty = GetSingleRequiredProperty(toolDefinition?.Parameters);

        if (string.IsNullOrWhiteSpace(requiredProperty) ||
            string.Equals(requiredProperty, "value", StringComparison.Ordinal))
        {
            return;
        }

        toolCall.Arguments = new JsonObject
        {
            [requiredProperty] = NormalizeSingleArgumentValue(requiredProperty, rawValue)
        };
    }

    private static string? GetSingleRequiredProperty(JsonObject? parameters)
    {
        if (parameters?["required"] is not JsonArray required || required.Count != 1)
        {
            return null;
        }

        return required[0]?.GetValue<string>();
    }

    private static string NormalizeSingleArgumentValue(string propertyName, string rawValue)
    {
        if (!propertyName.Contains("path", StringComparison.OrdinalIgnoreCase))
        {
            return rawValue;
        }

        var quotedPath = Regex.Match(rawValue, @"['""](?<path>/[^'""]+)['""]");
        if (quotedPath.Success)
        {
            return quotedPath.Groups["path"].Value;
        }

        var path = Regex.Match(rawValue, @"(?<path>/\S+)");
        return path.Success ? path.Groups["path"].Value.TrimEnd('.', ',', ';', ':') : rawValue;
    }

    private static void NormalizeEditArguments(
        AgentToolCall toolCall,
        IReadOnlyList<AgentToolDefinition> toolDefinitions,
        IReadOnlyList<AgentMessage> chatHistory)
    {
        if (toolCall.Arguments is not JsonObject arguments ||
            !RequiresProperties(toolCall, toolDefinitions, "old_string", "new_string"))
        {
            return;
        }

        if (!arguments.ContainsKey("new_string") &&
            arguments.TryGetPropertyValue("content", out var contentNode))
        {
            arguments["new_string"] = NormalizeStringArgument(contentNode);
            arguments.Remove("content");
        }

        if (arguments.TryGetPropertyValue("new_string", out var newStringNode))
        {
            arguments["new_string"] = NormalizeStringArgument(newStringNode);
        }

        if (arguments.TryGetPropertyValue("old_string", out var oldStringNode))
        {
            arguments["old_string"] = NormalizeStringArgument(oldStringNode);
        }

        if (!arguments.ContainsKey("new_string"))
        {
            return;
        }

        var filePath = arguments["file_path"]?.ToString();
        var latestReadContent = FindLastReadFileContent(chatHistory, filePath);
        if (string.IsNullOrEmpty(latestReadContent))
        {
            RewriteEditAsReadFile(toolCall, toolDefinitions, filePath);
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

    private static bool RequiresProperties(
        AgentToolCall toolCall,
        IReadOnlyList<AgentToolDefinition> toolDefinitions,
        params string[] propertyNames)
    {
        var toolDefinition = toolDefinitions.FirstOrDefault(tool =>
            string.Equals(tool.Name, toolCall.ToolName, StringComparison.Ordinal));

        if (toolDefinition?.Parameters?["required"] is not JsonArray required)
        {
            return false;
        }

        var requiredNames = required
            .Select(node => node?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        return propertyNames.All(requiredNames.Contains);
    }

    private static string NormalizeStringArgument(JsonNode? node)
    {
        var value = node?.ToString() ?? string.Empty;

        while (value.Length >= 2 &&
               value[0] == '"' &&
               value[^1] == '"')
        {
            try
            {
                var unwrapped = JsonNode.Parse(value)?.GetValue<string>();
                if (string.IsNullOrEmpty(unwrapped) || unwrapped == value)
                {
                    return value;
                }

                value = unwrapped;
            }
            catch
            {
                return value[1..^1];
            }
        }

        return value;
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

            return NormalizeStringArgument(valueNode);
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
