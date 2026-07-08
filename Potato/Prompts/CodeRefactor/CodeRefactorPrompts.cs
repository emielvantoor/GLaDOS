namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition RefactorSystem = new(
        "refactor-system.md",
        "You generate exact SEARCH/REPLACE patches. " +
        "Return the patch blocks only. " +
        "Do not include markdown fences or commentary.");

    public static string RefactorSystemPrompt => Load(RefactorSystem);

    public static string BuildRefactorUserPrompt(string filePath, string fileContent, string instructions) =>
        $$$"""
           You are the Refactor phase of Potato.
           Return exactly one SEARCH/REPLACE patch and nothing else.
           The SEARCH text must be copied exactly from the provided full file content.
           The SEARCH block must be large enough to match exactly once.
           The REPLACE block must contain the complete replacement text.
           If no safe exact patch can be made, return empty SEARCH and REPLACE blocks.

           Required format:
           <SEARCH>
           exact existing text
           </SEARCH>
           <REPLACE>
           complete replacement text
           </REPLACE>

           Target file:
           {{{filePath}}}

           Instructions:
           {{{instructions}}}

           Full file content:
           ```
           {{{fileContent}}}
           ```
           """;
}