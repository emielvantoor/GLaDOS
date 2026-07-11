using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;

namespace Potato.Session.Tasks;

public class DesignTask : AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "design";

    public override IReadOnlyList<string> PlanningGuidance =>
    [
        "Use design before implementation when the request leaves meaningful product, API, architecture, UX, or implementation choices open.",
        "Do not use design for small localized edits where the requested change and approach are already clear.",
        "Before design, read or inspect the smallest relevant context needed to make the tradeoffs concrete.",
        "A design task does NOT modify files directly; it produces a decision blueprint for later create-file, apply-patch, write-code, write-documentation, shell-script, or write-report steps."
    ];

    public async Task<string> ExecuteTaskAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.DesignSystemPrompt),
            new(
                ChatRole.User,
                Prompts.PromptLibrary.BuildDesignUserPrompt(
                    goal,
                    task.Argument,
                    context.LastReadFilePath ?? "(none)",
                    context.LastReadFileContent ?? "(none)",
                    observations.FormatObservations()))
        };

        ChatResponse response;
        const double DesignTemperature = 0.8;

        using (PotatoConsole.StartProgress("Designing implementation approach..."))
        {
            response = await chatClient.GetResponseAsync(
                messages,
                CreateChatOptions(DesignTemperature),
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            return "Error: Design task returned an empty blueprint.";
        }

        return $"{StringHelper.ReplanRequiredMarker}{Environment.NewLine}Design blueprint:{Environment.NewLine}{response.Text.Trim()}";
    }
}
