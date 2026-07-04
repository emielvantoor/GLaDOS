using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GLaDOS.Core.Models;

namespace GLaDOS.Core.ToolAdapters;

public class SingleValueArgumentAdapter : ToolCallAdapter
{
    public override bool CanAdapt(AgentToolCall toolCall, ToolCallAdapterContext context)
    {
        return toolCall.Arguments is JsonObject arguments &&
               arguments.Count == 1 &&
               arguments.TryGetPropertyValue("value", out var valueNode) &&
               valueNode is JsonValue value &&
               value.TryGetValue<string>(out _);
    }

    public override void Adapt(AgentToolCall toolCall, ToolCallAdapterContext context)
    {
        if (toolCall.Arguments is not JsonObject arguments ||
            !arguments.TryGetPropertyValue("value", out var valueNode) ||
            valueNode is not JsonValue value ||
            !value.TryGetValue<string>(out var rawValue))
        {
            return;
        }

        var toolDefinition = FindToolDefinition(toolCall, context);
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
}
