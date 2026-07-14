using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Tools;

namespace Potato.Session.Tasks;

public class CreateNewFileTask(AgentTools agentTools): AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "create-file";

    public async Task<string> ExecuteTaskAsync(
        string goal,
        AgentTask task, 
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        string filePath = task.Argument.Trim();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: create-file requires a concrete file path in the Argument property.";
        }

        string result = await agentTools.CreateFileAsync(filePath, string.Empty);
        if (!StringHelper.IsFailureResult(result))
        {
            context.LastReadFilePath = PathResolver.ResolveMentionedPath(filePath) ?? filePath;
            context.LastReadFileContent = string.Empty;
        }

        return result;
    }
}
