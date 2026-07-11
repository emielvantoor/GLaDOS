using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;

namespace Potato.Session.Tasks;

public class ArchitectRefactorTask : AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "architect-refactor";

    public override IReadOnlyList<string> PlanningGuidance =>
    [
        "Use architect-refactor to think about complex architectural changes, design patterns, or multi-method refactoring.",
        "This task does NOT modify files directly; it generates a blueprint.",
        "Always follow this task with a patch or write task to actually apply the planned changes."
    ];

    public async Task<string> ExecuteTaskAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.LastReadFilePath) ||
            string.IsNullOrWhiteSpace(context.LastReadFileContent))
        {
            return "Error: architect-refactor requires a successful read step first.";
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.ArchitectRefactorSystemPrompt),
            new(
                ChatRole.User,
                Prompts.PromptLibrary.BuildArchitectRefactorUserPrompt(
                    goal,
                    context.LastReadFilePath,
                    context.LastReadFileContent,
                    task.Argument,
                    observations.FormatObservations()))
        };

        ChatResponse response;
        const double ArchitecturalCreativity = 0.4;

        using (PotatoConsole.StartProgress(
                   $"Architecting refactor design for {PathResolver.FormatPathForDisplay(context.LastReadFilePath)}..."))
        {
            response = await chatClient.GetResponseAsync(
                messages,
                CreateChatOptions(ArchitecturalCreativity),
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            return "Error: Architect phase returned an empty blueprint.";
        }

        return $"{StringHelper.ReplanRequiredMarker}{Environment.NewLine}{response.Text.Trim()}";
    }
}
