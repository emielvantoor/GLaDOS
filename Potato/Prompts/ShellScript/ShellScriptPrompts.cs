namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition ShellScriptSystem = new(
        "shell-script-system-v2.md",
        "You generate one permissioned shell script for Potato. " +
        "Return ONLY minified JSON with these properties: command, workingDirectory, timeoutSeconds. " +
        "Do not use Markdown. Do not explain. " +
        "Use PowerShell syntax on Windows and Bash syntax on Linux/macOS. " +
        "Generate exactly one shell operation. " +
        "Never combine multiple operations with &&, ||, ;, pipes, redirection, or multiple lines. " +
        "Prefer the narrowest command that satisfies only the planner task argument. " +
        "Do not use shell for reading project files, listing directories, creating text files, or editing text files when Potato has a direct task for that. " +
        "Use shell only for operations not covered by direct Potato tasks, such as creating directories, running build/test commands, or invoking project tools. " +
        "Do not generate destructive commands unless the user request explicitly requires them.");

    private static readonly PromptDefinition ShellScriptUser = new(
        "shell-script-user.md",
        """
        Operating system: {{OperatingSystem}}
        Current directory: {{CurrentDirectory}}
        Home directory: {{HomeDirectory}}

        Original user request:
        {{UserRequest}}

        Planner task argument:
        {{TaskArgument}}

        Execution observations so far:
        {{ExecutionObservations}}
        """);

    public static string ShellScriptSystemPrompt => Load(ShellScriptSystem);

    public static string BuildShellScriptUserPrompt(
        string operatingSystem,
        string currentDirectory,
        string homeDirectory,
        string userRequest,
        string taskArgument,
        string executionObservations) =>
        Render(ShellScriptUser, new Dictionary<string, string>
        {
            ["OperatingSystem"] = operatingSystem,
            ["CurrentDirectory"] = currentDirectory,
            ["HomeDirectory"] = homeDirectory,
            ["UserRequest"] = userRequest,
            ["TaskArgument"] = taskArgument,
            ["ExecutionObservations"] = executionObservations
        });
}
