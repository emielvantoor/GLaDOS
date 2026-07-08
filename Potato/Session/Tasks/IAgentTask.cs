using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.Models;

namespace Potato.Session.Tasks;

public interface IAgentTask
{
    string ActionName { get; }

    IReadOnlyList<string> PlanningGuidance { get; }

    bool CanExecute(string targetAction);

    Task<string> ExecuteTaskAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken);
}
