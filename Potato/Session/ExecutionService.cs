using Microsoft.Extensions.AI;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Session.Tasks;

namespace Potato.Session;

public class ExecutionService(ExecutionMemory executionMemory, ICollection<IAgentTask> agentTasks)
{
    public async Task<ExecutionResult> ExecutePlanAsync(
        string goal,
        IReadOnlyList<AgentTask> tasks,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var observations = new List<TaskObservation>();
        var context = new ExecutorContext();
        executionMemory.Clear();

        foreach (AgentTask task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PotatoConsole.WriteStatus($"Executing step {task.Step}: {task.Action} {task.Argument}");

            try
            {
                string result = await ExecuteTaskAsync(goal, task, context, observations, chatClient, cancellationToken);
                observations.Add(new TaskObservation(task.Step, task.Action, task.Argument, result));

                if (StringHelper.IsFailureResult(result))
                {
                    return ExecutionResult.Failed(observations, $"Step {task.Step} failed: {StringHelper.FirstLine(result)}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                observations.Add(new TaskObservation(task.Step, task.Action, task.Argument, $"Error: {ex.Message}"));
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
            return $"Error: Unsupported planner action '{task.Action}'. Supported actions: read, refactor_prompt, write_report.";
        }

        return await agentTask.ExecuteTaskAsync(goal, task, context, observations, chatClient, cancellationToken);
    }
}