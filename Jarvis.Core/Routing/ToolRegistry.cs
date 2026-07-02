using Jarvis.Core.Interfaces;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Routing;

public class ToolRegistry
{
    private readonly Dictionary<string, IJarvisTool> _tools;

    public ToolRegistry(IEnumerable<IJarvisTool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name);
    }

    public IReadOnlyList<AgentToolDefinition> GetDefinitions()
    {
        return _tools.Values
            .Select(t => new AgentToolDefinition(t.Name, t.Description, t.Parameters))
            .ToList();
    }

    public bool TryGetInternalTool(string name, out IJarvisTool tool)
    {
        return _tools.TryGetValue(name, out tool!);
    }
}
