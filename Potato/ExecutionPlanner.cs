using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Potato;

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
            new(ChatRole.System, Prompts.PromptLibrary.ExecutionPlanningSystemPrompt),
            new(ChatRole.User,
                Prompts.PromptLibrary.BuildExecutionPlanningUserPrompt(
                    operatingSystem,
                    Environment.CurrentDirectory,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    latestUserRequest ?? "(unknown)",
                    latestSpecification ?? "(unknown)",
                    latestApproach ?? "(unknown)"))
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
