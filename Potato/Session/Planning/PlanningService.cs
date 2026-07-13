using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.Models;

namespace Potato.Session;

public class PlanningService(
    ProjectMapBuilder projectMapBuilder,
    PlanningArtifactGenerator artifactGenerator,
    PlannerTaskGenerator taskGenerator)
{
    public Task<List<AgentTask>> PlanAsync(string goal, IChatClient chatClient, CancellationToken cancellationToken) =>
        PlanAsync(goal, [], chatClient, cancellationToken);

    public async Task<List<AgentTask>> PlanAsync(
        string goal,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        string workspaceContext = await BuildProjectMapAsync(Environment.CurrentDirectory, chatClient, cancellationToken);
        string workspaceFileIndex = ProjectMapIndexFormatter.BuildWorkspaceFileIndex(workspaceContext);
        string planningSpec = await artifactGenerator.GeneratePlanningSpecAsync(
            goal,
            workspaceFileIndex,
            chatClient,
            cancellationToken);
        string draftPlan = await artifactGenerator.GenerateApprovedDraftPlanAsync(
            goal,
            planningSpec,
            workspaceFileIndex,
            chatClient,
            cancellationToken);

        return await taskGenerator.GenerateTaskListAsync(
            goal,
            observations,
            workspaceContext,
            workspaceFileIndex,
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
}
