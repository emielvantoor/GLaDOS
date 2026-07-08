namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition ProjectMapSystem = new(
        "project-map-system.md",
        "You summarize source and project files for a repository map. Return concise bullets only.");

    private static readonly PromptDefinition ProjectMapUser = new(
        "project-map-user.md",
        """
        Summarize this source or project file for a repository map.
        Return under 3 bullet points.
        Include the file's purpose, programming language or project type, and key public methods/types/components/configuration.
        Do not include markdown fences or large code quotes.

        File path:
        {{FilePath}}

        File contents:
        ```
        {{FileContent}}
        ```
        """);

    public static string BuildProjectMapSystemPrompt =>
        Load(ProjectMapSystem);
    
    public static string BuildProjectMapUserPrompt(string filePath, string fileContent) =>
        Render(ProjectMapUser, new Dictionary<string, string>
        {
            ["FilePath"] = filePath,
            ["FileContent"] = fileContent
        });
}
