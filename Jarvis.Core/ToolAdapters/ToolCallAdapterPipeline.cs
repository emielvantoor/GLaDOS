using Jarvis.Core.Models;

namespace Jarvis.Core.ToolAdapters;

public class ToolCallAdapterPipeline
{
    private readonly IReadOnlyList<IToolCallAdapter> _adapters;

    public ToolCallAdapterPipeline(IEnumerable<IToolCallAdapter> adapters)
    {
        _adapters = adapters.ToList();
    }

    public void Adapt(AgentToolCall toolCall, ToolCallAdapterContext context)
    {
        foreach (var adapter in _adapters)
        {
            if (adapter.CanAdapt(toolCall, context))
            {
                adapter.Adapt(toolCall, context);
            }
        }
    }
}
