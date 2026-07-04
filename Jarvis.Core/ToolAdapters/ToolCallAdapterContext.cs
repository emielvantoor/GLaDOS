using Jarvis.Core.Interfaces;

namespace Jarvis.Core.ToolAdapters;

public sealed record ToolCallAdapterContext(
    IReadOnlyList<AgentToolDefinition> ToolDefinitions,
    IReadOnlyList<AgentMessage> ChatHistory);
