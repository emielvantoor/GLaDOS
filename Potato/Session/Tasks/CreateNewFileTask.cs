using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Tools;

namespace Potato.Session.Tasks;

public class CreateNewFileTask(AgentTools agentTools): AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "create-file";

    public override IReadOnlyList<string> PlanningGuidance =>
    [
        "Use create-file only to create an empty missing file path before a later content-writing task.",
        "For create-file, the Argument must be the concrete file path to create, not a description of the implementation.",
        "After create-file for a source or asset file, plan read for that new file and then write-code with the full implementation instructions.",
        "After create-file for a Markdown documentation file, plan write-documentation with the target path and documentation requirements.",
        "If a requested Markdown target such as FEATURE.md is absent from Workspace context, plan create-file for that exact path before write-documentation targets it.",
        "For repository-level README requests where README.md is absent from Workspace context, use create-file with Argument \"README.md\" after inspect-project, then write-documentation."
    ];

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
