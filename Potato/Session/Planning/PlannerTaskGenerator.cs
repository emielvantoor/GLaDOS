using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Session.Tasks;

namespace Potato.Session;

public sealed class PlannerTaskGenerator(
    IEnumerable<IAgentTask> agentTasks,
    PlanTaskNormalizer taskNormalizer)
{
    private const int MaxPlannerRepairAttempts = 10;

    public async Task<List<AgentTask>> GenerateTaskListAsync(
        string goal,
        IReadOnlyList<TaskObservation> observations,
        string workspaceContext,
        string workspaceFileIndex,
        string planningSpec,
        string draftPlan,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> supportedActions = GetSupportedActions();
        IReadOnlyList<string> planningGuidance = GetPlanningGuidance();
        string executionObservations = observations.FormatObservations();
        Exception? lastPlanningError = null;
        var plannerRepairMessages = new List<string>();

        for (int attempt = 1; attempt <= MaxPlannerRepairAttempts; attempt++)
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, Prompts.PromptLibrary.PlannerSystemPrompt),
                new(ChatRole.User, Prompts.PromptLibrary.BuildPlannerUserPrompt(
                    goal,
                    workspaceFileIndex,
                    planningSpec,
                    draftPlan,
                    supportedActions,
                    planningGuidance,
                    executionObservations))
            };

            ChatResponse response;
            using (PotatoConsole.StartProgress(FormatPlanningProgress(attempt)))
            {
                response = await chatClient.GetResponseAsync(messages, AgentTaskBase.CreateJsonChatOptions(0.0),
                    cancellationToken);
            }

            try
            {
                string json = PlanningJsonExtractor.ExtractJsonArray(response.Text);
                List<AgentTask>? tasks = JsonSerializer.Deserialize<List<AgentTask>>(json, AgentTaskBase.JsonOptions);
                if (tasks is null || tasks.Count == 0)
                {
                    throw new InvalidOperationException("Planner returned no tasks.");
                }

                return taskNormalizer.Normalize(tasks, goal, workspaceContext, observations);
            }
            catch (InvalidOperationException ex)
            {
                lastPlanningError = ex;
                plannerRepairMessages.Add(ex.Message);
                if (attempt == MaxPlannerRepairAttempts)
                {
                    break;
                }

                PotatoConsole.WriteStatus($"Planner response repair needed: {ex.Message}");
                executionObservations = BuildPlannerRetryObservations(observations, plannerRepairMessages);
            }
        }

        throw new InvalidOperationException(
            $"Planner could not produce a valid task list after {MaxPlannerRepairAttempts} attempts: {lastPlanningError?.Message}");
    }

    private static string FormatPlanningProgress(int attempt) =>
        attempt == 1
            ? "Planning deterministic task list..."
            : $"Repairing planner task list ({attempt}/{MaxPlannerRepairAttempts})...";

    private static string BuildPlannerRetryObservations(
        IReadOnlyList<TaskObservation> observations,
        IReadOnlyList<string> repairMessages)
    {
        var builder = new StringBuilder();
        string formattedObservations = observations.FormatObservations();
        if (!string.Equals(formattedObservations, "(none)", StringComparison.Ordinal))
        {
            builder.AppendLine(formattedObservations.TrimEnd());
            builder.AppendLine();
        }

        builder.AppendLine("Planner response repair feedback from previous attempts:");
        foreach ((string repairMessage, int index) in repairMessages.Select((message, index) => (message, index)))
        {
            builder.AppendLine($"{index + 1}. {repairMessage}");
        }

        builder.AppendLine("Return a corrected JSON array that completes the user request and addresses all repair feedback above.");
        return builder.ToString();
    }

    private IReadOnlyList<string> GetSupportedActions() =>
        agentTasks
            .Select(task => task.ActionName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(action => action, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private IReadOnlyList<string> GetPlanningGuidance() =>
        agentTasks
            .OrderBy(task => task.ActionName, StringComparer.OrdinalIgnoreCase)
            .SelectMany(task => task.PlanningGuidance)
            .Where(guidance => !string.IsNullOrWhiteSpace(guidance))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
