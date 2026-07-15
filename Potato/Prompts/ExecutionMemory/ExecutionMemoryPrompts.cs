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

    private static readonly PromptDefinition ExecutionMemorySummaryGoalAware = new(
        "execution-memory-summary-goal-aware.md",
        """
        You are summarizing a tool result to help achieve this goal:
        **Goal**: {{Goal}}

        Analyze this content and create a goal-aware summary:

        Source: {{Source}}
        Content:
        {{Content}}

        Provide a summary that includes:
        1. **Relevance**: Is this content relevant to the goal? (state explicitly: "Yes, directly relevant" OR "Partially relevant" OR "Not relevant")
        2. **Key findings**: What key facts/structure help achieve the goal? (2-4 bullets)
        3. **Gaps**: What important info is missing or unclear?
        4. **Retrieval guidance**: If full content is needed, explain what specific info requires it (or say "Summary sufficient")
        5. **Confidence**: [Confidence: HIGH/MEDIUM/LOW] - Can the model proceed with just this summary for the goal?

        Keep summary concise (100-150 tokens). End with the confidence marker.
        
        Example format:
        **Relevance**: Yes, directly relevant - contains UI component styles for your goal
        **Key findings**:
        - Responsive grid layout (.raw-grid) with mobile/tablet queries
        - 11 CSS color variables for portal themes
        - Component styles: .composer, .header-wrapper, .input-area
        **Gaps**: Exact hex color values not shown in summary
        **Retrieval guidance**: Summary sufficient for layout purposes. Full content needed only if specific color hex codes required.
        [Confidence: HIGH]
        """);

    public static string BuildExecutionMemorySummaryUserPrompt(string source, string content) =>
        Render(ExecutionMemorySummaryUser, new Dictionary<string, string>
        {
            ["Source"] = source,
            ["Content"] = content
        });

    public static string BuildExecutionMemorySummaryGoalAwarePrompt(string goal, string source, string content) =>
        Render(ExecutionMemorySummaryGoalAware, new Dictionary<string, string>
        {
            ["Goal"] = goal,
            ["Source"] = source,
            ["Content"] = content
        });
}
