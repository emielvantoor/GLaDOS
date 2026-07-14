using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.Models;

namespace Potato.Session.Tasks;

public class SearchProjectMapTask(ProjectMapBuilder projectMapBuilder) : AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "search-project-map";

    public override IReadOnlyList<string> PlanningGuidance =>
    [
        "Use search-project-map when the compact Workspace context does not list the file needed for a read, edit, documentation, or architecture task.",
        "Use search-project-map before read when the user names a file, class, feature, folder, or concept and the exact indexed path is not already known.",
        "For search-project-map, put a focused query in Argument, optionally using this format: Query: <keywords, file name, class, feature, or folder>\nMax results: <1-30>.",
        "After search-project-map returns File entries, plan read or edit tasks only for exact paths returned by that search result."
    ];

    public async Task<string> ExecuteTaskAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        (string query, int maxResults) = ParseArgument(task.Argument);
        if (string.IsNullOrWhiteSpace(query))
        {
            query = goal;
        }

        return await projectMapBuilder.SearchProjectMapAsync(
            Environment.CurrentDirectory,
            query,
            maxResults,
            chatClient,
            cancellationToken);
    }

    private static (string Query, int MaxResults) ParseArgument(string argument)
    {
        string normalized = argument.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return (string.Empty, 12);
        }

        string query = normalized;
        int maxResults = 12;
        foreach (string line in normalized.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Query:", StringComparison.OrdinalIgnoreCase))
            {
                query = line["Query:".Length..].Trim();
            }
            else if (line.StartsWith("Max results:", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(line["Max results:".Length..].Trim(), out int parsedMaxResults))
            {
                maxResults = parsedMaxResults;
            }
        }

        return (query, maxResults);
    }
}
