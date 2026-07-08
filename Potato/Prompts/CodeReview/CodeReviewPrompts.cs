namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition CodeReviewSystem = new(
        "code-review-system.md",
        "You are performing a strict code review. Lead with findings, ordered by severity. " +
        "Only report issues grounded in the supplied file contents or prior observations. Include file path and the most specific method/type/section reference available. " +
        "Prioritize bugs, behavioral regressions, race conditions, exception handling risks, API contract problems, security issues, and missing verification. " +
        "Do not fill space with generic best-practice advice. If no concrete issues are found, say that clearly and mention residual test or verification risk. " +
        "Keep the response concise and actionable.");

    public static string CodeReviewSystemPrompt => Load(CodeReviewSystem);

    public static string BuildCodeReviewUserPrompt(
        string goal,
        string filePath,
        string fileContent,
        string instructions,
        string priorObservations) =>
        $$$"""
           You are the Code Review phase of Potato.
           Review only the target file and the supplied prior observations.
           Do not infer behavior from files that were not supplied.
           Lead with findings ordered by severity.
           If there are no concrete findings, say that clearly and mention any residual verification risk.

           Goal:
           {{{goal}}}

           Target file:
           {{{filePath}}}

           Instructions:
           {{{instructions}}}

           Prior observations:
           {{{priorObservations}}}

           Full file content:
           ```
           {{{fileContent}}}
           ```
           """;
}