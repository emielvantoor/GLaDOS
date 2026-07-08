namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition ExecutionPlanningSystem = new(
        "execution-planning-system.md",
        "You decide whether the approved approach requires one shell command. " +
        "If it does, return ONLY minified JSON with these properties: command, workingDirectory, timeoutSeconds. " +
        "If it does not require shell execution, return ONLY minified JSON with an empty command: {\"command\":\"\",\"workingDirectory\":null,\"timeoutSeconds\":60}. " +
        "Do not use Markdown. Do not explain. " +
        "Use PowerShell syntax on Windows and Bash syntax on Linux/macOS. " +
        "For inspection/listing tasks, prefer read-only commands. " +
        "Do not generate destructive commands unless the approved task explicitly requires destructive changes.");

    private static readonly PromptDefinition ExecutionPlanningUser = new(
        "execution-planning-user.md",
        """
        Operating system: {{OperatingSystem}}
        Current directory: {{CurrentDirectory}}
        Home directory: {{HomeDirectory}}

        Original user request:
        {{UserRequest}}

        Approved specification:
        {{Specification}}

        Approved approach:
        {{Approach}}
        """);

    public static string ExecutionPlanningSystemPrompt => Load(ExecutionPlanningSystem);

    public static string BuildExecutionPlanningUserPrompt(
        string operatingSystem,
        string currentDirectory,
        string homeDirectory,
        string userRequest,
        string specification,
        string approach) =>
        Render(ExecutionPlanningUser, new Dictionary<string, string>
        {
            ["OperatingSystem"] = operatingSystem,
            ["CurrentDirectory"] = currentDirectory,
            ["HomeDirectory"] = homeDirectory,
            ["UserRequest"] = userRequest,
            ["Specification"] = specification,
            ["Approach"] = approach
        });
}
