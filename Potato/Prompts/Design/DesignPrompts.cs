namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition DesignSystem = new(
        "design/design-system.md",
        """
        You are Potato's design phase. Explore the meaningful choices in the requested work before implementation.
        Use the supplied repository observations and last-read file only; do not invent project facts.
        Make a concrete decision, explain the tradeoffs briefly, and produce an implementation-ready blueprint.
        Do not write code blocks unless a compact interface or data shape is necessary to remove ambiguity.
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
