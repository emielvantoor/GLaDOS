using System.Text.Json.Nodes;
using Jarvis.Core.Interfaces;
using Jarvis.Core.Models;

namespace Jarvis.Core.Protocols;

public class OpenAIProtocol : IAgentProtocol
{
    public string Name => "OpenAI";

    public bool SupportsThinking => false;

    public string BuildPrompt(List<AgentMessage> history, IReadOnlyList<AgentToolDefinition> tools)
    {
        return new QwenProtocol().BuildPrompt(history, tools);
    }

    public IEnumerable<AgentToolCall> ParseResponse(string response)
    {
        var toolCall = new AgentToolCall
        {
            Provider = Name,
            RawCall = response
        };

        try
        {
            var node = JsonNode.Parse(response);
            var name = node?["name"]?.ToString()
                ?? node?["function"]?["name"]?.ToString()
                ?? string.Empty;

            if (string.IsNullOrEmpty(name))
            {
                return [];
            }

            toolCall.ToolName = name;
            toolCall.Arguments = NormalizeArguments(node?["arguments"]);
            return [toolCall];
        }
        catch
        {
            return [];
        }
    }

    public string BuildToolResponse(AgentToolCall toolCall, string toolResult)
    {
        return toolResult;
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
            try
            {
                return JsonNode.Parse(raw) ?? new JsonObject();
            }
            catch
            {
                return new JsonObject { ["value"] = raw };
            }
        }

        return arguments.DeepClone();
    }
}
