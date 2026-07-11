namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition WriteDocumentationSystem = new(
        "write-documentation/write-documentation-system.md",
        "You are Potato's Technical Writer. Write comprehensive, clear, professional Markdown documentation. " +
        "Focus on accurate structure, useful examples, and concise prose. " +
        "Output only raw Markdown content. Do not include markdown fences, explanations, or conversational text.");

    private static readonly PromptDefinition WriteDocumentationUser = new(
        "write-documentation/write-documentation-user.md",
        """
        Write or completely replace the requested documentation file.
        Return the full Markdown file content and nothing else.

        Rules:
        - Write for the target audience and requirements supplied by the task.
        - Use prior observations as factual source material.
        - Do not invent files, commands, APIs, configuration keys, or behavior not supported by the goal or prior observations.
        - Prefer clear headings, short paragraphs, and concrete examples when they help the reader.
        - Keep the content appropriate for the target file path.
        - Return raw Markdown, not JSON and not a diff.

        Goal:
        {{Goal}}

        Target file:
        {{FilePath}}

        Documentation requirements:
        {{Requirements}}

        Prior observations:
        {{PriorObservations}}
        """);

    public static string WriteDocumentationSystemPrompt => Load(WriteDocumentationSystem);

    public static string BuildWriteDocumentationUserPrompt(
        string goal,
        string filePath,
        string requirements,
        string priorObservations) =>
        Render(WriteDocumentationUser, new Dictionary<string, string>
        {
            ["Goal"] = goal,
            ["FilePath"] = filePath,
            ["Requirements"] = requirements,
            ["PriorObservations"] = priorObservations
        });
}
