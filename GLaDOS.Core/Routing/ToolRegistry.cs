using GLaDOS.Core.Interfaces;
using GLaDOS.Core.Tools;

namespace GLaDOS.Core.Routing;

public class ToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools;

    public ToolRegistry(IEnumerable<IAgentTool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name);
    }

    public IReadOnlyList<AgentToolDefinition> GetDefinitions()
    {
        return _tools.Values
            .Select(t => new AgentToolDefinition(t.Name, t.Description, t.Parameters, t.Permitted))
            .ToList();
    }

    public bool TryGetInternalTool(string name, out IAgentTool tool)
    {
        return _tools.TryGetValue(name, out tool!);
    }
}
