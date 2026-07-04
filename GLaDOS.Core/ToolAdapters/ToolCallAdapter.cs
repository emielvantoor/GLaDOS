using System.Text.Json.Nodes;
using GLaDOS.Core.Interfaces;
using GLaDOS.Core.Models;

namespace GLaDOS.Core.ToolAdapters;

public abstract class ToolCallAdapter : IToolCallAdapter
{
    public abstract bool CanAdapt(AgentToolCall toolCall, ToolCallAdapterContext context);

    public abstract void Adapt(AgentToolCall toolCall, ToolCallAdapterContext context);

    protected static AgentToolDefinition? FindToolDefinition(
        AgentToolCall toolCall,
        ToolCallAdapterContext context)
    {
        return context.ToolDefinitions.FirstOrDefault(tool =>
            string.Equals(tool.Name, toolCall.ToolName, StringComparison.Ordinal));
    }

    protected static bool RequiresProperties(
        AgentToolCall toolCall,
        ToolCallAdapterContext context,
        params string[] propertyNames)
    {
        var toolDefinition = FindToolDefinition(toolCall, context);
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

    protected static string? GetSingleRequiredProperty(JsonObject? parameters)
    {
        if (parameters?["required"] is not JsonArray required || required.Count != 1)
        {
            return null;
        }

        return required[0]?.GetValue<string>();
    }
}
