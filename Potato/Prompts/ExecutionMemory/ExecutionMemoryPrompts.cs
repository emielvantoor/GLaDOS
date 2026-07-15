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
        5. **Confidence Assessment**: Determine if the model can COMPLETE THE GOAL with just this summary:

           HIGH: Summary is SUFFICIENT for goal completion
           - Example: Task "Extract all CSS color variables" + Summary shows "11 CSS variables: #eef1f2, #f9faf8, ..." → HIGH
           - Example: Task "Is .composer class present?" + Summary shows ".composer found at line X" → HIGH
           - Example: Task "Analyze theme colors" + Summary lists "Light: 8 colors, Dark: 8 colors defined" → HIGH

           MEDIUM: Summary is PARTIAL, might need full content for complete accuracy
           - Example: Task "Extract all CSS rules" + Summary shows "~50 rules total, showing 3 key ones" → MEDIUM (more in full file)
           - Example: Task "Find all grid layouts" + Summary shows "grid-template in 2+ places" → MEDIUM (might be more)
           - Example: Task "Audit styles" + Summary shows overview but no completeness guarantee → MEDIUM

           LOW: Summary is INSUFFICIENT for goal completion
           - Example: Task "Extract ALL CSS components" + Summary shows only first 15 lines → LOW (need full 1500 lines)
           - Example: Task "Count total functions" + Summary shows partial sample → LOW (need full to count accurately)
           - Example: Task "Refactor structure" + Summary too incomplete for comprehensive changes → LOW

        Keep summary concise (100-150 tokens). End with confidence marker format:
        [Confidence: HIGH/MEDIUM/LOW • Task-Reason: "{{GOAL}} - {{why this confidence}}"]
        
        Example format:
        **Relevance**: Yes, directly relevant - contains CSS components
        **Key findings**:
        - Total: ~50 CSS rules (color vars, components, layouts, responsive)
        - Showing: First 15 lines (color variables) + last 15 lines (responsive)
        - Components detected: .composer, .header-wrapper, .grid-layout, etc.
        **Gaps**: Middle section omitted (components, animations, utilities)
        **Retrieval guidance**: For extraction/analysis, full file needed to capture all ~50 rules.
        [Confidence: LOW • Task-Reason: "Extract ALL components - only edges shown, need full 1500-line file"]
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
