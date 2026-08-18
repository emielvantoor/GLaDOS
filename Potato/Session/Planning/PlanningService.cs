using Microsoft.Extensions.AI;
using Potato.Prompts;

namespace Potato.Session;

public class PlanningService(ProjectMapBuilder projectMapBuilder)
{
    public string BuildDirectExecutionGuidance(string currentDirectory) =>
        PromptLibrary.BuildDirectExecutionGuidance(currentDirectory);

    public Task<string> BuildProjectMapHeaderAsync(string targetDirectory, CancellationToken cancellationToken) =>
        projectMapBuilder.BuildProjectMapHeaderAsync(targetDirectory, cancellationToken);

    public Task<string> SearchProjectMapAsync(
        string targetDirectory,
        string query,
        int maxResults,
        IChatClient? chatClient,
        CancellationToken cancellationToken) =>
        projectMapBuilder.SearchProjectMapAsync(targetDirectory, query, maxResults, chatClient, cancellationToken);
}
