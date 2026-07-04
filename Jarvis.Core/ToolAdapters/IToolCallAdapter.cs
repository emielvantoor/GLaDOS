using Jarvis.Core.Models;

namespace Jarvis.Core.ToolAdapters;

public interface IToolCallAdapter
{
    bool CanAdapt(AgentToolCall toolCall, ToolCallAdapterContext context);

    void Adapt(AgentToolCall toolCall, ToolCallAdapterContext context);
}
