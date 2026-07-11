using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Tools;

namespace Potato.Session.Tasks;

public class WriteCodeTask(AgentTools agentTools) : AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "write-code";

    public override IReadOnlyList<string> PlanningGuidance =>
    [
        "Use write-code only after reading the existing file that should be fully implemented or completed.",
        "Use write-code for broad implementation work inside one existing file; use apply-patch for small localized edits.",
        "For write-code, put the edit target and instructions in Argument using this format: Target file: <exact path from Workspace context>\nInstructions: <concrete implementation instructions>.",
        "Never use write-code to create a missing file; use create-file instead."
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
            context.LastReadFileContent is null)
        {
            return "Error: write-code requires a successful read step first.";
        }

        (string filePath, string fileContent, string instructions) = ResolveWriteTarget(task, context);
        string replacement = await GenerateReplacementAsync(
            goal,
            filePath,
            fileContent,
            instructions,
            observations,
            chatClient,
            cancellationToken);

        if (string.Equals(fileContent, replacement, StringComparison.Ordinal))
        {
            return "No changes: write-code generated content identical to the current file.";
        }

        return await agentTools.ApplySearchReplaceAsync(filePath, fileContent, replacement);
    }

    private (string FilePath, string FileContent, string Instructions) ResolveWriteTarget(
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
            throw new InvalidOperationException($"Could not read explicit write-code target '{targetFilePath}': {StringHelper.FirstLine(content)}");
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

    private async Task<string> GenerateReplacementAsync(
        string goal,
        string filePath,
        string fileContent,
        string instructions,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.WriteCodeSystemPrompt),
            new(
                ChatRole.User,
                Prompts.PromptLibrary.BuildWriteCodeUserPrompt(
                    goal,
                    filePath,
                    fileContent,
                    instructions,
                    observations.FormatObservations()))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress($"Generating implementation for {PathResolver.FormatPathForDisplay(filePath)}..."))
        {
            response = await chatClient.GetResponseAsync(
                messages,
                CreateChatOptions(0.0),
                cancellationToken);
        }

        string replacement = StringHelper.StripCodeFence(response.Text);
        if (string.IsNullOrWhiteSpace(replacement))
        {
            throw new InvalidOperationException("Write-code model returned an empty file.");
        }

        return replacement;
    }
}
