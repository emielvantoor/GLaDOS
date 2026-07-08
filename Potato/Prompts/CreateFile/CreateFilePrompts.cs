namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition CreateFileSystem = new(
        "create-file-system.md",
        "You are the Create File phase of Potato. Return exactly one strict JSON object and nothing else. " +
        "The object must have exactly these properties: filePath and content. " +
        "Use the file path requested by the task argument. " +
        "Files mentioned in observations are reference material only unless the task argument names that same path. " +
        "Do not import from or couple to a reference project unless the user explicitly asked for that dependency. " +
        "Do not include markdown fences or commentary.");

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

        Rules:
        - The Create task value is the file path to create.
        - Generate content for the target project named by that path.
        - Treat Last read file and Prior observations as reference context, not as files to edit or dependencies to import.
        - If this file defines a supporting component for an entry point, make the component complete enough for the entry point to compile without relying on uncreated classes.

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
