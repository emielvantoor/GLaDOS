using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Tools;

namespace Potato.Session.Tasks;

public class RefactorTask(AgentTools agentTools) : AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "refactor-prompt";

    public override IReadOnlyList<string> PlanningGuidance =>
    [
        "Use refactor-prompt only after reading the file that should be changed.",
        "For refactor-prompt, put only the concrete edit instructions in Argument."
    ];
    
    public async Task<string> ExecuteTaskAsync(string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.LastReadFilePath) ||
            string.IsNullOrWhiteSpace(context.LastReadFileContent))
        {
            return "Error: refactor_prompt requires a successful read step first.";
        }

        SearchReplacePatch patch = await GenerateRefactorPatchAsync(
            goal,
            task,
            context.LastReadFilePath,
            context.LastReadFileContent,
            observations,
            chatClient,
            cancellationToken);

        return await agentTools.ApplySearchReplaceAsync(context.LastReadFilePath, patch.Search, patch.Replace);
    }

    private async Task<SearchReplacePatch> GenerateRefactorPatchAsync(
        string goal,
        AgentTask task,
        string filePath,
        string fileContent,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.RefactorSystemPrompt),
            new(
                ChatRole.User,
                $"Goal:\n{goal}\n\n" +
                "Prior observations:\n" +
                observations.FormatObservations() +
                "\n\n" +
                Prompts.PromptLibrary.BuildRefactorUserPrompt(filePath, fileContent, task.Argument))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress(
                   $"Generating refactor patch for {PathResolver.FormatPathForDisplay(filePath)}..."))
        {
            response = await chatClient.GetResponseAsync(
                messages,
                CreateChatOptions(0.0),
                cancellationToken);
        }

        SearchReplacePatch patch = ParseSearchReplaceBlocks(response.Text);

        if (string.IsNullOrEmpty(patch.Search) || patch.Replace is null)
        {
            throw new InvalidOperationException("Refactor model did not return valid SEARCH/REPLACE blocks.");
        }

        if (!fileContent.Contains(patch.Search, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refactor model returned SEARCH text that is not present in the full file content.");
        }

        return patch with { FilePath = filePath };
    }
}
