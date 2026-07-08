namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition SideQuestionSystem = new(
        "side-question.md",
        "You are answering a side question in the Potato CLI. " +
        "Answer directly and concisely. Do not use the planner/executor workflow. " +
        "Do not ask for execution approval. Do not call tools.");

    public static string SideQuestionSystemPrompt => Load(SideQuestionSystem);
}