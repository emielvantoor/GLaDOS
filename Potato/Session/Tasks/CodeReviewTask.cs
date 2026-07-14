using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;

namespace Potato.Session.Tasks;

public class CodeReviewTask : AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "code_review";

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
            return "Error: review_code requires a successful read step first.";
        }
    
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.CodeReviewSystemPrompt),
            new(
                ChatRole.User,
                Prompts.PromptLibrary.BuildCodeReviewUserPrompt(
                    goal,
                    context.LastReadFilePath,
                    context.LastReadFileContent,
                    task.Argument,
                    observations.FormatObservations()))
        };
    
        ChatResponse response;
        using (PotatoConsole.StartProgress($"Reviewing {PathResolver.FormatPathForDisplay(context.LastReadFilePath)}..."))
        {
            response = await chatClient.GetResponseAsync(
                messages, AgentTaskBase.CreateChatOptions(task.GetTargetTemperature()),
                cancellationToken);
        }
    
        return string.IsNullOrWhiteSpace(response.Text)
            ? "Error: Code review returned an empty response."
            : response.Text.Trim();
    }
}
