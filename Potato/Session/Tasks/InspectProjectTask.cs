using System.Text;
using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.Models;
using Potato.Tools;

namespace Potato.Session.Tasks;

public class InspectProjectTask(AgentTools agentTools) : AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "inspect-project";

    public Task<string> ExecuteTaskAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(InspectProject(task.Argument));
    }

    private string InspectProject(string directoryPath)
    {
        string targetDirectory = string.IsNullOrWhiteSpace(directoryPath)
            ? "."
            : directoryPath;

        var builder = new StringBuilder();
        builder.AppendLine($"Project inspection: {targetDirectory}");
        builder.AppendLine();
        builder.AppendLine("Top-level files and folders:");
        builder.AppendLine(agentTools.ListFiles(targetDirectory, recursive: false, maxEntries: 300));
        builder.AppendLine();
        builder.AppendLine("Project manifests:");
        builder.AppendLine(agentTools.ListProjectFiles(targetDirectory));
        builder.AppendLine();
        builder.AppendLine("Likely source, documentation, and test files:");
        builder.AppendLine(agentTools.SearchFiles(
            ".sln|.csproj|.fsproj|.vbproj|package.json|pyproject.toml|Cargo.toml|go.mod|pom.xml|build.gradle|README|.md|.cs|.fs|.ts|.js|test|tests|src|source",
            targetDirectory,
            recursive: true,
            maxMatches: 300));

        return builder.ToString().TrimEnd();
    }
}
