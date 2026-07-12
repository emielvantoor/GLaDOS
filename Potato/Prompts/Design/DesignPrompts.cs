namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition DesignSystem = new(
        "design/design-system.md",
        """
        You are Potato's design phase and an expert Frontend Engineer. Your goal is to explore the meaningful choices in the requested work before implementation and design a beautiful, modern UI.
        Use the supplied repository observations and last-read file only; do not invent project facts.
        Make a concrete decision, explain the tradeoffs briefly, and produce an implementation-ready blueprint.
        Do not write code blocks unless a compact interface or data shape is necessary to remove ambiguity.

        CRITICAL DESIGN RULES:
        1. You MUST use shadcn utility classes.
        2. Use a modern, dark-mode-first color palette (e.g., slate-900 background, emerald-500 for primary accents).
        3. Always use generous spacing (padding/margin) and rounded corners (rounded-xl or rounded-2xl).
        4. Never use raw, unstyled HTML elements. Everything must look premium and clean, like a modern SaaS dashboard.
        """);

    private static readonly PromptDefinition DesignUser = new(
        "design/design-user.md",
        """
        Global Goal:
        {{Goal}}

        Design Objective:
        {{Instructions}}

        Last read file:
        {{LastReadFile}}

        Last read file contents:
        ```
        {{LastReadFileContent}}
        ```

        Prior pipeline observations:
        {{PriorObservations}}

        Return:
        - Recommended direction
        - Key tradeoffs considered
        - Implementation constraints
        - Concrete next steps for the executor
        """);

    public static string DesignSystemPrompt => Load(DesignSystem);

    public static string BuildDesignUserPrompt(
        string goal,
        string instructions,
        string lastReadFile,
        string lastReadFileContent,
        string priorObservations) =>
        Render(DesignUser, new Dictionary<string, string>
        {
            ["Goal"] = goal,
            ["Instructions"] = instructions,
            ["LastReadFile"] = lastReadFile,
            ["LastReadFileContent"] = lastReadFileContent,
            ["PriorObservations"] = priorObservations
        });
}
