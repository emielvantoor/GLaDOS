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
                    return ExecutionResult.Failed(observations, $"Step {task.Step} failed: {StringHelper.FirstLine(result)}");
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
                    PotatoConsole.WriteStatus($"Adaptive replan {replanCount}/{MaxAdaptiveReplans} requested by step {task.Step}.");
                    activeTasks = await planningService.PlanAsync(goal, observations, chatClient, cancellationToken);
                    taskIndex = 0;
                    continue;
                }

                taskIndex++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                observations.Add(new TaskObservation(observations.Count + 1, task.Action, task.Argument, $"Error: {ex.Message}"));
                return ExecutionResult.Failed(observations, $"Step {task.Step} threw an exception: {ex.Message}");
            }
        }

        return ExecutionResult.Succeeded(observations);
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
}
