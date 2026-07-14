using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.Models;

namespace Potato.Session;

public class PlanningService(
    ProjectMapBuilder projectMapBuilder,
    PlanningArtifactGenerator artifactGenerator,
    PlannerTaskGenerator taskGenerator)
{
    public string BuildDirectExecutionGuidance(string goal, string currentDirectory) =>
        $"""
        Direct execution guidance:
        - Work in small ReAct steps: choose one tool, read the observation, then choose the next tool.
        - Current working directory: {currentDirectory}
        - Use list-files, list-project-files, search-files, search-file-contents, or search-project-map for discovery before reading exact files.
        - Use search-project-map when the user names a feature, symbol, component, or likely file that is not already confirmed by a tool observation.
        - For edits, read the latest file content first, then use ApplySearchReplaceAsync, CreateFileAsync, or ApplyDiffPatchAsync.
        - Do not use shell commands to edit text files. Use shell only for explicit command requests or verification commands.
        - Return FINAL only after the requested work is complete and, for project changes, at least one edit tool has reported success.

        User goal:
        {goal}
        """;

    public Task<List<AgentTask>> PlanAsync(string goal, IChatClient chatClient, CancellationToken cancellationToken) =>
        PlanAsync(goal, [], chatClient, cancellationToken);

    public async Task<List<AgentTask>> PlanAsync(
        string goal,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        string workspaceContext = await projectMapBuilder.BuildProjectMapHeaderAsync(Environment.CurrentDirectory, cancellationToken);
        string workspacePlanningContext = ProjectMapIndexFormatter.BuildWorkspacePlanningContext(
            workspaceContext,
            Environment.CurrentDirectory);
        string planningSpec = await artifactGenerator.GeneratePlanningSpecAsync(
            goal,
            workspacePlanningContext,
            chatClient,
            cancellationToken);
        string draftPlan = await artifactGenerator.GenerateApprovedDraftPlanAsync(
            goal,
            planningSpec,
            workspacePlanningContext,
            chatClient,
            cancellationToken);

        return await taskGenerator.GenerateTaskListAsync(
            goal,
            observations,
            workspaceContext,
            workspacePlanningContext,
            planningSpec,
            draftPlan,
            chatClient,
            cancellationToken);
    }

    public Task<string> BuildProjectMapAsync(
        string targetDirectory,
        IChatClient chatClient,
        CancellationToken cancellationToken) =>
        projectMapBuilder.BuildProjectMapAsync(targetDirectory, chatClient, cancellationToken);

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
