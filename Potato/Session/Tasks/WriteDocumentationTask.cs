using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Tools;

namespace Potato.Session.Tasks;

public class WriteDocumentationTask(AgentTools agentTools) : AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "write-documentation";

    public override IReadOnlyList<string> PlanningGuidance =>
    [
        "Use write-documentation only for Markdown files that already appear in Workspace context or were created by an earlier create-file step in the same plan.",
        "Never use write-documentation as the first step for a missing Markdown file; first plan create-file with that exact Markdown path, then plan write-documentation if a dedicated documentation pass is needed.",
        "Never use write-documentation for source or asset files such as .html, .css, .js, .ts, .cs, .json, .xml, .svg, or .png; use create-file for new source files, or apply-patch/write-code for existing source files.",
        "For simple requests against an existing or earlier-created Markdown file, set Argument to the exact destination file path (e.g., 'README.md').",
        "For detailed requests, put the edit target and requirements in Argument using this format: Target file: <exact path>\nRequirements: <target audience, scope, and layout requirements>."
    ];

    public async Task<string> ExecuteTaskAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        (string filePath, string requirements) = ResolveDocumentationTarget(task);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: write-documentation requires a target file path in the Argument property.";
        }

        if (!IsDocumentationPath(filePath))
        {
            return $"Error: write-documentation can only target Markdown documentation files, not '{filePath}'. Use apply-patch or write-code for source files.";
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.WriteDocumentationSystemPrompt),
            new(
                ChatRole.User,
                Prompts.PromptLibrary.BuildWriteDocumentationUserPrompt(
                    goal,
                    filePath,
                    requirements,
                    observations.FormatObservations()))
        };

        ChatResponse response;

        using (PotatoConsole.StartProgress($"Generating documentation for {PathResolver.FormatPathForDisplay(filePath)}..."))
        {
            response = await chatClient.GetResponseAsync(
                messages,
                CreateChatOptions(0.7),
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            return "Error: Documentation task returned an empty response.";
        }

        string generatedMarkdown = StringHelper.StripCodeFence(response.Text).Trim();

        try
        {
            string writeResult = await agentTools.OverwriteFileAsync(filePath, generatedMarkdown);
            if (StringHelper.IsFailureResult(writeResult))
            {
                return writeResult;
            }

            return $"Success: Successfully wrote documentation to {filePath} ({generatedMarkdown.Length} characters).";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to write documentation file to disk: {ex.Message}";
        }
    }

    private static (string FilePath, string Requirements) ResolveDocumentationTarget(AgentTask task)
    {
        if (!TryExtractDocumentationTarget(task.Argument, out string? targetFilePath, out string requirements))
        {
            return (task.Argument.Trim(), task.Reason);
        }

        return (targetFilePath!, requirements);
    }

    private static bool TryExtractDocumentationTarget(string argument, out string? targetFilePath, out string requirements)
    {
        targetFilePath = null;
        requirements = string.Empty;

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

        const string requirementsPrefix = "Requirements:";
        int requirementsIndex = normalized.IndexOf(requirementsPrefix, StringComparison.OrdinalIgnoreCase);
        requirements = requirementsIndex >= 0
            ? normalized[(requirementsIndex + requirementsPrefix.Length)..].Trim()
            : normalized[(pathEnd < 0 ? normalized.Length : pathEnd)..].Trim();
        targetFilePath = path;
        return true;
    }

    private static bool IsDocumentationPath(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFileName(path), "README", StringComparison.OrdinalIgnoreCase);
}
