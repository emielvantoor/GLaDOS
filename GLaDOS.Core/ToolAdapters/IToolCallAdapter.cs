using GLaDOS.Core.Models;

namespace GLaDOS.Core.ToolAdapters;

public interface IToolCallAdapter
{
    bool CanAdapt(AgentToolCall toolCall, ToolCallAdapterContext context);

    void Adapt(AgentToolCall toolCall, ToolCallAdapterContext context);
}
