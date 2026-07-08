namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition PatchSystem = new(
        "patch-system.md",
        "You are the Patch phase of Potato. You receive one specific code block selected by the executor. " +
        "Return exactly one strict JSON object and nothing else. The object must have exactly these properties: filePath, search, replace. " +
        "The search value must be copied exactly from the provided code block and must be large enough to match only once. " +
        "The replace value must be the complete replacement for that search text. " +
        "Do not use markdown fences. Do not include commentary. Do not patch outside the provided code block. " +
        "If no safe exact search/replace can be made from the provided code block, return {\"filePath\":\"\",\"search\":\"\",\"replace\":\"\"}.");

    public static string PatchSystemPrompt => Load(PatchSystem);
}