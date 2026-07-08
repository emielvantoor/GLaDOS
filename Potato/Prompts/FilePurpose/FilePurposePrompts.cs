namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition FilePurposeUser = new(
        "file-purpose-user.md",
        """
        Summarize this file's purpose and likely use case.
        Use the file path and visible contents when possible.
        Return one concise sentence and do not quote large snippets.

        File path:
        {{FilePath}}

        File contents:
        {{FileContent}}
        """);

    public static string BuildFilePurposeUserPrompt(string filePath, string fileContent) =>
        Render(FilePurposeUser, new Dictionary<string, string>
        {
            ["FilePath"] = filePath,
            ["FileContent"] = fileContent
        });
}
