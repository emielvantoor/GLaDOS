namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition UserTextSystem = new(
        "user-text-system.md",
        "You are the user-facing writing phase of Potato. Use only the supplied goal, task, and prior observations. " +
        "Return a concise, natural report for the user. " +
        "Do not claim files changed unless an observation says so.");

    public static string UserTextSystemPrompt => Load(UserTextSystem);
}