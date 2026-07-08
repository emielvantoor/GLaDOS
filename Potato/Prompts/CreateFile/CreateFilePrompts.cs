namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition CreateFileSystem = new(
        "create-file-system.md",
        "You are the Create File phase of Potato. Return exactly one strict JSON object and nothing else. " +
        "The object must have exactly these properties: filePath and content. " +
        "Use the file path requested by the task argument. Do not include markdown fences or commentary.");

    private static readonly PromptDefinition CreateFileUser = new(
        "create-file-user.md",
        """
        Return a JSON object only.

        Goal:
        {{Goal}}

        Create task:
        {{Task}}

        Last read file:
        {{LastReadFile}}

        Prior observations:
        {{PriorObservations}}
        """);

    public static string CreateFileSystemPrompt => Load(CreateFileSystem);

    public static string BuildCreateFileUserPrompt(
        string goal,
        string task,
        string lastReadFile,
        string priorObservations) =>
        Render(CreateFileUser, new Dictionary<string, string>
        {
            ["Goal"] = goal,
            ["Task"] = task,
            ["LastReadFile"] = lastReadFile,
            ["PriorObservations"] = priorObservations
        });
}
