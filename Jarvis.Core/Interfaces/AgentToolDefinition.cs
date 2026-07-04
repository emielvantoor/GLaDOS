using Jarvis.Core.Tools;

namespace Jarvis.Core.Interfaces;

public record AgentToolDefinition(
    string Name,
    string Description,
    System.Text.Json.Nodes.JsonObject? Parameters,
    ToolPermission Permitted = ToolPermission.User);
