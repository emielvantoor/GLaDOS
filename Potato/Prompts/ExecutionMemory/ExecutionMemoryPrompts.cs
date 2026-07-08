namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition ExecutionMemorySummaryUser = new(
        "execution-memory-summary-user.md",
        """
        Summarize this collected execution context for later retrieval.
        Keep concrete file names, commands, errors, dependencies, and conclusions.
        Use concise bullets.

        Source:
        {{Source}}

        Content:
        {{Content}}
        """);

    public static string BuildExecutionMemorySummaryUserPrompt(string source, string content) =>
        Render(ExecutionMemorySummaryUser, new Dictionary<string, string>
        {
            ["Source"] = source,
            ["Content"] = content
        });
}
