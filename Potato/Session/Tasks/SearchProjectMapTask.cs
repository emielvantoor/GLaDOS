using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.Models;

namespace Potato.Session.Tasks;

public class SearchProjectMapTask(ProjectMapBuilder projectMapBuilder) : AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "search-project-map";

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
