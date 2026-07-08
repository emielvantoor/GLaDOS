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
        "When reading another file only as a reference, read the reference first and then read the target file immediately before refactor-prompt.",
        "For refactor-prompt, put the edit target and instructions in Argument using this format: Target file: <exact path from Workspace context or prior create-file>\nInstructions: <concrete edit instructions>.",
        "Never use refactor-prompt to edit a file that was described as a reference."
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

        (string filePath, string fileContent, string instructions) = ResolveRefactorTarget(task, context);

        SearchReplacePatch patch = await GenerateRefactorPatchAsync(
            goal,
            task,
            filePath,
            fileContent,
            instructions,
            observations,
            chatClient,
            cancellationToken);

        return await agentTools.ApplySearchReplaceAsync(filePath, patch.Search, patch.Replace);
    }

    private (string FilePath, string FileContent, string Instructions) ResolveRefactorTarget(
        AgentTask task,
        ExecutorContext context)
    {
        string instructions = task.Argument;
        if (!TryExtractTargetFile(task.Argument, out string? targetFilePath, out string? targetInstructions))
        {
            return (context.LastReadFilePath!, context.LastReadFileContent!, instructions);
        }

        instructions = targetInstructions;
        string? resolvedTarget = PathResolver.ResolveMentionedPath(targetFilePath!);
        string? resolvedLastRead = PathResolver.ResolveMentionedPath(context.LastReadFilePath!);
        if (resolvedTarget is not null &&
            resolvedLastRead is not null &&
            string.Equals(resolvedTarget, resolvedLastRead, StringComparison.OrdinalIgnoreCase))
        {
            return (context.LastReadFilePath!, context.LastReadFileContent!, instructions);
        }

        string content = agentTools.ReadFileContent(targetFilePath!);
        if (StringHelper.IsFailureResult(content))
        {
            throw new InvalidOperationException($"Could not read explicit refactor target '{targetFilePath}': {StringHelper.FirstLine(content)}");
        }

        return (targetFilePath!, content, instructions);
    }

    private static bool TryExtractTargetFile(string argument, out string? targetFilePath, out string instructions)
    {
        targetFilePath = null;
        instructions = argument;

        string normalized = argument.Replace("\r\n", "\n", StringComparison.Ordinal);
        const string targetPrefix = "Target file:";
        int targetIndex = normalized.IndexOf(targetPrefix, StringComparison.OrdinalIgnoreCase);
        if (targetIndex < 0)
        {
            return false;
        }

        int pathStart = targetIndex + targetPrefix.Length;
        int pathEnd = normalized.IndexOf('\n', pathStart);
        string path = (pathEnd < 0 ? normalized[pathStart..] : normalized[pathStart..pathEnd]).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        const string instructionsPrefix = "Instructions:";
        int instructionsIndex = normalized.IndexOf(instructionsPrefix, StringComparison.OrdinalIgnoreCase);
        instructions = instructionsIndex >= 0
            ? normalized[(instructionsIndex + instructionsPrefix.Length)..].Trim()
            : normalized[(pathEnd < 0 ? normalized.Length : pathEnd)..].Trim();
        targetFilePath = path;
        return true;
    }

    private async Task<SearchReplacePatch> GenerateRefactorPatchAsync(
        string goal,
        AgentTask task,
        string filePath,
        string fileContent,
        string instructions,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.RefactorSystemPrompt),
            new(
                ChatRole.User,
                Prompts.PromptLibrary.BuildRefactorUserPrompt(
                    goal,
                    filePath,
                    fileContent,
                    instructions,
                    observations.FormatObservations()))
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
