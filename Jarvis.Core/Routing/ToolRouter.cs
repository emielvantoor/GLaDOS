using System.Text.Json.Nodes;
using Jarvis.Core.Models;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Routing;

public class ToolRouter
{
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger<ToolRouter> _logger;

    public ToolRouter(ToolRegistry toolRegistry, ILogger<ToolRouter> logger)
    {
        _toolRegistry = toolRegistry;
        _logger = logger;
    }

    public async Task<AgentToolResult> RouteAsync(AgentToolCall toolCall)
    {
        if (!_toolRegistry.TryGetInternalTool(toolCall.ToolName, out var tool))
        {
            _logger.LogInformation("Delegating external tool call {ToolName}", toolCall.ToolName);
            return new AgentToolResult
            {
                ToolCall = toolCall,
                Output = string.Empty,
                IsExternal = true
            };
        }

        if (tool.Permitted == ToolPermission.User)
        {
            _logger.LogInformation("Delegating user-permitted internal tool call {ToolName}", toolCall.ToolName);
            return new AgentToolResult
            {
                ToolCall = toolCall,
                Output = string.Empty,
                IsExternal = true
            };
        }

        var arguments = toolCall.Arguments as JsonObject ?? new JsonObject();
        var output = await tool.ExecuteAsync(arguments);

        _logger.LogInformation("Executed internal tool {ToolName}", toolCall.ToolName);
        return new AgentToolResult
        {
            ToolCall = toolCall,
            Output = output,
            IsExternal = false
        };
    }
}
