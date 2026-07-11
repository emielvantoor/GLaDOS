namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition ArchitectRefactorSystem = new(
        "architect-refactor/architect-refactor-system.md",
        "You are Potato's Lead Software Architect. Your job is to analyze the provided code and design an elegant, " +
        "maintainable refactoring plan. Do not worry about exact search/replace syntax yet. Focus entirely on " +
        "clean architecture, SOLID principles, and correct logic. Provide your solution as a clear Markdown blueprint.");

    private static readonly PromptDefinition ArchitectRefactorUser = new(
        "architect-refactor/architect-refactor-user.md",
        """
        Global Goal:
        {{Goal}}

        Specific Refactor Objective:
        {{Instructions}}

        Target File Path:
        {{FilePath}}

        Current File Contents:
        ```csharp
        {{FileContent}}
        ```

        Prior pipeline observations:
        {{PriorObservations}}
        """);

    public static string ArchitectRefactorSystemPrompt => Load(ArchitectRefactorSystem);

    public static string BuildArchitectRefactorUserPrompt(
        string goal,
        string filePath,
        string fileContent,
        string instructions,
        string priorObservations) =>
        Render(ArchitectRefactorUser, new Dictionary<string, string>
        {
            ["Goal"] = goal,
            ["FilePath"] = filePath,
            ["Instructions"] = instructions,
            ["PriorObservations"] = priorObservations,
            ["FileContent"] = fileContent
        });
}
