namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition UserTextSystem = new(
        "user-text-system.md",
        "You are the user-facing writing phase of Potato. Use only the supplied goal, task, and prior observations. " +
        "Return a concise, natural report for the user. " +
        "Do not claim files changed unless an observation says so.");

    private static readonly PromptDefinition UserTextUser = new(
        "user-text-user.md",
        """
        Action: {{Action}}
        Temperature: {{Temperature}}

        Goal:
        {{Goal}}

        Task:
        {{Task}}

        Last read file:
        {{LastReadFile}}

        Prior observations:
        {{PriorObservations}}
        """);

    public static string UserTextSystemPrompt => Load(UserTextSystem);

    public static string BuildUserTextUserPrompt(
        string action,
        string temperature,
        string goal,
        string task,
        string lastReadFile,
        string priorObservations) =>
        Render(UserTextUser, new Dictionary<string, string>
        {
            ["Action"] = action,
            ["Temperature"] = temperature,
            ["Goal"] = goal,
            ["Task"] = task,
            ["LastReadFile"] = lastReadFile,
            ["PriorObservations"] = priorObservations
        });
}
