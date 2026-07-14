using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Tools;

namespace Potato.Session.Tasks;

public class ReadFileTask(AgentTools agentTools) : AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "read";

    public Task<string> ExecuteTaskAsync(string goal, AgentTask task, ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient, CancellationToken cancellationToken)
    {
        return Task.FromResult(ReadFile(task.Argument, context));
    }

    private string ReadFile(string filePath, ExecutorContext context)
    {
        string result = agentTools.ReadFileContent(filePath);
        if (!StringHelper.IsFailureResult(result))
        {
            context.LastReadFilePath = filePath;
            context.LastReadFileContent = result;
        }

        return result;
    }
}
