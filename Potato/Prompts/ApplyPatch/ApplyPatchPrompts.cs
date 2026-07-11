namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition ApplyPatchSystem = new(
        "apply-patch-system.md",
        "You generate exact SEARCH/REPLACE patches. " +
        "Return the patch blocks only. " +
        "Do not include markdown fences or commentary.");

    private static readonly PromptDefinition ApplyPatchUser = new(
        "apply-patch-user.md",
        """
        You are the Apply Patch phase of Potato.
        Return exactly one SEARCH/REPLACE patch and nothing else.
        The SEARCH text must be copied exactly from the provided full file content.
        The SEARCH block must be large enough to match exactly once.
        The REPLACE block must contain the complete replacement text.
        Only edit the Target file content provided below.
        Files in Prior observations are reference context only; do not patch them and do not import from them unless the instructions explicitly require that dependency.
        Do not introduce references to new classes, methods, namespaces, or files unless Prior observations show they already exist or were created earlier in this execution.
        If the requested edit requires missing supporting files or classes, return empty SEARCH and REPLACE blocks instead of producing uncompilable code.
        If no safe exact patch can be made, return empty SEARCH and REPLACE blocks.

        Goal:
        {{Goal}}

        Prior observations:
        {{PriorObservations}}

        Required format:
        <SEARCH>
        exact existing text
        </SEARCH>
        <REPLACE>
        complete replacement text
        </REPLACE>

        Target file:
        {{FilePath}}

        Instructions:
        {{Instructions}}

        Full file content:
        ```
        {{FileContent}}
        ```
        """);

    

    public static string ApplyPatchSystemPrompt => Load(ApplyPatchSystem);


    public static string BuildApplyPatchUserPrompt(
        string goal,
        string filePath,
        string fileContent,
        string instructions,
        string priorObservations) =>
        Render(ApplyPatchUser, new Dictionary<string, string>
        {
            ["Goal"] = goal,
            ["FilePath"] = filePath,
            ["Instructions"] = instructions,
            ["PriorObservations"] = priorObservations,
            ["FileContent"] = fileContent
        });

   
}
