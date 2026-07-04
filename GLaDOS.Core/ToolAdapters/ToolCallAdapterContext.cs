using GLaDOS.Core.Interfaces;

namespace GLaDOS.Core.ToolAdapters;

public sealed record ToolCallAdapterContext(
    IReadOnlyList<AgentToolDefinition> ToolDefinitions,
    IReadOnlyList<AgentMessage> ChatHistory);
