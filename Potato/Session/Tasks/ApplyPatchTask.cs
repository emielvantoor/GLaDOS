using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Tools;

namespace Potato.Session.Tasks;

public class ApplyPatchTask(AgentTools agentTools) : AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "apply-patch";

    public override IReadOnlyList<string> PlanningGuidance =>
    [
        $"Use {Name} only after reading the file that should be changed.",
        $"When reading another file only as a reference, read the reference first and then read the target file immediately before {Name}.",
        $"For {Name}, put the edit target and instructions in Argument using this format: Target file: <exact path from Workspace context or prior create-file>\nInstructions: <concrete edit instructions>.",
        $"Never use {Name} to edit a file that was described as a reference."
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
            return $"Error: {Name} requires a successful read step first.";
        }

        (string filePath, string fileContent, string instructions) = ResolvePatchTarget(task, context);

        SearchReplacePatch patch = TryBuildDeterministicPatch(filePath, fileContent, instructions) ??
                                    await GeneratePatchAsync(
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

    private (string FilePath, string FileContent, string Instructions) ResolvePatchTarget(
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
            throw new InvalidOperationException($"Could not read explicit patch target '{targetFilePath}': {StringHelper.FirstLine(content)}");
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

    private async Task<SearchReplacePatch> GeneratePatchAsync(
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
            new(ChatRole.System, Prompts.PromptLibrary.ApplyPatchSystemPrompt),
            new(
                ChatRole.User,
                Prompts.PromptLibrary.BuildApplyPatchUserPrompt(
                    goal,
                    filePath,
                    fileContent,
                    instructions,
                    observations.FormatObservations()))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress(
                   $"Generating patch for {PathResolver.FormatPathForDisplay(filePath)}..."))
        {
            response = await chatClient.GetResponseAsync(
                messages,
                CreateChatOptions(0.0),
                cancellationToken);
        }

        SearchReplacePatch patch = ParseSearchReplaceBlocks(response.Text);

        if (string.IsNullOrEmpty(patch.Search) || patch.Replace is null)
        {
            throw new InvalidOperationException("Apply patch model did not return valid SEARCH/REPLACE blocks.");
        }

        patch = NormalizePatchLineEndingsForFile(fileContent, patch);
        if (!fileContent.Contains(patch.Search, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Apply patch model returned SEARCH text that is not present in the full file content.");
        }

        return patch with { FilePath = filePath };
    }

    private static SearchReplacePatch? TryBuildDeterministicPatch(
        string filePath,
        string fileContent,
        string instructions)
    {
        if (!IsMarkdownFenceRemovalRequest(instructions))
        {
            return null;
        }

        string replacement = RemoveMarkdownFenceLines(fileContent);
        if (string.Equals(replacement, fileContent, StringComparison.Ordinal))
        {
            return null;
        }

        return new SearchReplacePatch
        {
            FilePath = filePath,
            Search = fileContent,
            Replace = replacement
        };
    }

    private static bool IsMarkdownFenceRemovalRequest(string instructions)
    {
        return instructions.Contains("remove", StringComparison.OrdinalIgnoreCase) &&
               instructions.Contains("markdown", StringComparison.OrdinalIgnoreCase) &&
               instructions.Contains("fence", StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveMarkdownFenceLines(string content)
    {
        string lineEnding = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string normalized = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        string[] lines = normalized.Split('\n');
        bool hadTrailingNewline = normalized.EndsWith('\n');

        var keptLines = new List<string>(lines.Length);
        int lineCount = hadTrailingNewline ? lines.Length - 1 : lines.Length;
        for (int i = 0; i < lineCount; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.Equals("---", StringComparison.Ordinal))
            {
                continue;
            }

            keptLines.Add(lines[i]);
        }

        string result = string.Join(lineEnding, keptLines);
        return hadTrailingNewline ? result + lineEnding : result;
    }

    private static SearchReplacePatch NormalizePatchLineEndingsForFile(string fileContent, SearchReplacePatch patch)
    {
        if (fileContent.Contains(patch.Search, StringComparison.Ordinal))
        {
            return patch;
        }

        string lineEnding = fileContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string normalizedSearch = patch.Search.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", lineEnding, StringComparison.Ordinal);
        if (!fileContent.Contains(normalizedSearch, StringComparison.Ordinal))
        {
            return patch;
        }

        string normalizedReplace = (patch.Replace ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", lineEnding, StringComparison.Ordinal);
        return patch with
        {
            Search = normalizedSearch,
            Replace = normalizedReplace
        };
    }
}
