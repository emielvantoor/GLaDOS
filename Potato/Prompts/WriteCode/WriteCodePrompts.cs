namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition WriteCodeSystem = new(
        "code-refactor/write-code-system.md",
        "You are Potato's Write Code phase. Return the complete replacement file content only. " +
        "Do not include markdown fences, explanations, patch blocks, or conversational text.");

    private static readonly PromptDefinition WriteCodeUser = new(
        "code-refactor/write-code-user.md",
        """
        Rewrite the target file by completing the requested implementation.
        Return the complete replacement content for the target file and nothing else.

        Rules:
        - Preserve existing behavior that is unrelated to the instructions.
        - Keep namespaces, project style, and public contracts compatible unless the instructions require a change.
        - Do not reference new files, classes, packages, or APIs unless Prior observations show they already exist or were created earlier in this execution.
        - If supporting code is needed in this same file, include it in the replacement content.
        - Treat files in Prior observations as reference context only; only rewrite the Target file.
        - Return valid source text for the target file, not a diff and not JSON.

        Goal:
        {{Goal}}

        Prior observations:
        {{PriorObservations}}

        Target file:
        {{FilePath}}

        Instructions:
        {{Instructions}}

        Current file content:
        ```
        {{FileContent}}
        ```
        """);
    
    public static string BuildWriteCodeUserPrompt(
        string goal,
        string filePath,
        string fileContent,
        string instructions,
        string priorObservations) =>
        Render(WriteCodeUser, new Dictionary<string, string>
        {
            ["Goal"] = goal,
            ["FilePath"] = filePath,
            ["Instructions"] = instructions,
            ["PriorObservations"] = priorObservations,
            ["FileContent"] = fileContent
        });
    
    public static string WriteCodeSystemPrompt => Load(WriteCodeSystem);
    
}