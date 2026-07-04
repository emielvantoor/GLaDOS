using GLaDOS.Core.Tools;

namespace GLaDOS.Core.Interfaces;

public record AgentToolDefinition(
    string Name,
    string Description,
    System.Text.Json.Nodes.JsonObject? Parameters,
    ToolPermission Permitted = ToolPermission.User);
