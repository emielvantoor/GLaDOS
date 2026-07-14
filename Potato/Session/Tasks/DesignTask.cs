using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;

namespace Potato.Session.Tasks;

public class DesignTask : AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "design";

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
