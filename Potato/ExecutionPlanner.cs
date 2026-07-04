using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

internal sealed class ExecutionPlanner(IChatClient plannerClient)
{
    public async Task<ShellCommandPlan?> TryPlanExecutionAsync(
        string? latestUserRequest,
        string? latestSpecification,
        string? latestApproach)
    {
        string operatingSystem = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "macOS"
                : "Linux";

        var plannerMessages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You decide whether the approved approach requires one shell command. " +
                "If it does, return ONLY minified JSON with these properties: command, workingDirectory, timeoutSeconds. " +
                "If it does not require shell execution, return ONLY minified JSON with an empty command: {\"command\":\"\",\"workingDirectory\":null,\"timeoutSeconds\":60}. " +
                "Do not use Markdown. Do not explain. " +
                "Use PowerShell syntax on Windows and Bash syntax on Linux/macOS. " +
                "For inspection/listing tasks, prefer read-only commands. " +
                "Do not generate destructive commands unless the approved task explicitly requires destructive changes."),
            new(ChatRole.User,
                $"Operating system: {operatingSystem}\n" +
                $"Current directory: {Environment.CurrentDirectory}\n" +
                $"Home directory: {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\n\n" +
                $"Original user request:\n{latestUserRequest ?? "(unknown)"}\n\n" +
                $"Approved specification:\n{latestSpecification ?? "(unknown)"}\n\n" +
                $"Approved approach:\n{latestApproach ?? "(unknown)"}")
        };

        try
        {
            ChatResponse response = await plannerClient.GetResponseAsync(plannerMessages);
            string json = StripMarkdownFence(response.Text).Trim();
            ShellCommandPlan? plan;
            if (json.StartsWith("{", StringComparison.Ordinal))
            {
                plan = JsonSerializer.Deserialize<ShellCommandPlan>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            else
            {
                plan = new ShellCommandPlan { Command = json };
            }

            if (string.IsNullOrWhiteSpace(plan?.Command))
            {
                return null;
            }

            plan.Command = plan.Command.Trim();
            return plan;
        }
        catch
        {
            return null;
        }
    }

    private static string StripMarkdownFence(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int firstLineEnd = trimmed.IndexOf('\n');
        int lastFenceStart = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstLineEnd < 0 || lastFenceStart <= firstLineEnd)
        {
            return trimmed;
        }

        return trimmed[(firstLineEnd + 1)..lastFenceStart].Trim();
    }
}
