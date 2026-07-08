using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;

namespace Potato.Session.Tasks;

public class WriteReportTask : AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "write-report";

    public override IReadOnlyList<string> PlanningGuidance =>
    [
        "Use write-report when the user should receive findings or a summary."
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
            new(ChatRole.System, Prompts.PromptLibrary.UserTextSystemPrompt),
            new(
                ChatRole.User,
                "Action: write_report\n" +
                "Temperature: 0.7\n\n" +
                $"Goal:\n{goal}\n\n" +
                $"Task:\n{task.Argument}\n\n" +
                $"Last read file: {context.LastReadFilePath ?? "(none)"}\n\n" +
                "Prior observations:\n" +
                observations.FormatObservations())
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress($"Generating {task.Action} response..."))
        {
            response = await chatClient.GetResponseAsync(
                messages, CreateChatOptions(0.7),
                cancellationToken);
        }

        return string.IsNullOrWhiteSpace(response.Text)
            ? "Error: Text generation returned an empty response."
            : response.Text.Trim();
    }
}
