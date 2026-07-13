using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Session.Tasks;

namespace Potato.Session;

public class ExecutionService(
    ExecutionMemory executionMemory,
    IEnumerable<IAgentTask> agentTasks,
    PlanningService planningService)
{
    private const int MaxAdaptiveReplans = 3;

    public async Task<ExecutionResult> ExecutePlanAsync(
        string goal,
        IReadOnlyList<AgentTask> tasks,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var observations = new List<TaskObservation>();
        var context = new ExecutorContext();
        IReadOnlyList<AgentTask> activeTasks = tasks;
        int taskIndex = 0;
        int replanCount = 0;
        executionMemory.Clear();

        while (taskIndex < activeTasks.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AgentTask task = activeTasks[taskIndex];
            PotatoConsole.WriteStatus($"Executing step {task.Step}: {task.Action} {task.Argument}");

            try
            {
                string result = await ExecuteTaskAsync(goal, task, context, observations, chatClient, cancellationToken);
                observations.Add(new TaskObservation(observations.Count + 1, task.Action, task.Argument, result));

                if (StringHelper.IsFailureResult(result))
                {
                    if (!CanAdaptiveReplanAfterFailure(result))
                    {
                        return ExecutionResult.Failed(observations, $"Step {task.Step} failed: {StringHelper.FirstLine(result)}");
                    }

                    if (replanCount >= MaxAdaptiveReplans)
                    {
                        return ExecutionResult.Failed(
                            observations,
                            $"Step {task.Step} failed after {MaxAdaptiveReplans} adaptive replans: {StringHelper.FirstLine(result)}");
                    }

                    replanCount++;
                    PotatoConsole.WriteStatus($"Adaptive repair {replanCount}/{MaxAdaptiveReplans} after step {task.Step} failed.");
                    activeTasks = await RepairTaskListAsync(
                        goal,
                        activeTasks,
                        taskIndex,
                        observations,
                        chatClient,
                        cancellationToken);
                    continue;
                }

                if (StringHelper.IsReplanRequiredResult(result))
                {
                    if (replanCount >= MaxAdaptiveReplans)
                    {
                        return ExecutionResult.Failed(
                            observations,
                            $"Adaptive replanning stopped after {MaxAdaptiveReplans} replans.");
                    }

                    replanCount++;
                    PotatoConsole.WriteStatus($"Adaptive repair {replanCount}/{MaxAdaptiveReplans} requested by step {task.Step}.");
                    activeTasks = await RepairTaskListAsync(
                        goal,
                        activeTasks,
                        taskIndex,
                        observations,
                        chatClient,
                        cancellationToken);
                    continue;
                }

                taskIndex++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PotatoConsole.WriteError($"Step {task.Step} threw an exception: {ex.Message}");
                observations.Add(new TaskObservation(observations.Count + 1, task.Action, task.Argument, $"Error: {ex.Message}"));
                return ExecutionResult.Failed(observations, $"Step {task.Step} threw an exception: {ex.Message}");
            }
        }

        return ExecutionResult.Succeeded(observations);
    }

    private async Task<IReadOnlyList<AgentTask>> RepairTaskListAsync(
        string goal,
        IReadOnlyList<AgentTask> activeTasks,
        int failedTaskIndex,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        List<AgentTask> repairTasks = await planningService.PlanAsync(goal, observations, chatClient, cancellationToken);
        List<AgentTask> replacementTasks = SelectReplacementTasks(repairTasks, activeTasks, failedTaskIndex);

        List<AgentTask> repairedTasks = activeTasks
            .Take(failedTaskIndex)
            .Concat(replacementTasks)
            .Concat(activeTasks.Skip(failedTaskIndex + 1))
            .Select((task, index) => task with { Step = index + 1 })
            .ToList();

        string replacementSummary = string.Join(
            ", ",
            replacementTasks.Select(task => $"{task.Action} {StringHelper.FirstLine(task.Argument)}"));
        PotatoConsole.WriteStatus(
            replacementTasks.Count == 0
                ? "Adaptive repair produced no replacement tasks; continuing with remaining original tasks."
                : $"Replaced failed step {failedTaskIndex + 1} with {replacementTasks.Count} task(s): {replacementSummary}");

        return repairedTasks;
    }

    private static List<AgentTask> SelectReplacementTasks(
        IReadOnlyList<AgentTask> repairTasks,
        IReadOnlyList<AgentTask> activeTasks,
        int failedTaskIndex)
    {
        AgentTask[] orderedRepairTasks = repairTasks.OrderBy(task => task.Step).ToArray();
        bool hasOriginalTail = failedTaskIndex + 1 < activeTasks.Count;
        if (!hasOriginalTail)
        {
            return orderedRepairTasks.ToList();
        }

        AgentTask[] nonFinalReportTasks = orderedRepairTasks
            .Where(task => StringHelper.NormalizeAction(task.Action) != "write-report")
            .ToArray();

        return nonFinalReportTasks.Length == 0
            ? orderedRepairTasks.ToList()
            : nonFinalReportTasks.ToList();
    }

    private async Task<string> ExecuteTaskAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var action = StringHelper.NormalizeAction(task.Action);
        var agentTask = agentTasks.FirstOrDefault(a => a.CanExecute(action));
        if (agentTask == null)
        {
            string supportedActions = string.Join(", ", agentTasks.Select(task => task.ActionName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            return $"Error: Unsupported planner action '{task.Action}'. Supported actions: {supportedActions}.";
        }

        return await agentTask.ExecuteTaskAsync(goal, task, context, observations, chatClient, cancellationToken);
    }

    private static bool CanAdaptiveReplanAfterFailure(string result)
    {
        string firstLine = StringHelper.FirstLine(result);
        return !firstLine.Contains(" denied", StringComparison.OrdinalIgnoreCase) &&
               !firstLine.Contains("Unsupported planner action", StringComparison.OrdinalIgnoreCase);
    }
}
