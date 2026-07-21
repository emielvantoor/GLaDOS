using Microsoft.Extensions.AI;

namespace Potato.Session;

public class PlanningService(ProjectMapBuilder projectMapBuilder)
{
    public string BuildDirectExecutionGuidance(string currentDirectory) =>
        $"""
        Direct execution guidance:
        - Work in small ReAct steps: choose one tool, read the observation, then choose the next tool.
        - Current working directory: {currentDirectory}
        - Use list-files, list-project-files, search-files, search-file-contents, or search-project-map for discovery before reading exact files.
        - Use search-project-map when the user names a feature, symbol, component, or likely file that is not already confirmed by a tool observation.
        - For edits, read the latest file content first, then use ApplySearchReplaceAsync. Prefer short unique start/end anchors for large edits and exact search only for small substitutions. Use CreateFileAsync or ApplyDiffPatchAsync only when appropriate.
        - Do not use shell commands to edit text files. Use shell only for explicit command requests or verification commands.
        - Return FINAL only after the requested work is complete and, for project changes, at least one edit tool has reported success.
        """;

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
