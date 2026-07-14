using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Tools;

namespace Potato.Session.Tasks;

public class ShellScriptTask(AgentTools agentTools) : AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "shell-script";

    public async Task<string> ExecuteTaskAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        ShellCommandPlan plan = TryUseConcreteTaskCommand(task.Argument) ??
                                await GenerateShellCommandPlanAsync(goal, task, observations, chatClient, cancellationToken);
        if (string.IsNullOrWhiteSpace(plan.Command))
        {
            return "Error: Shell-script model returned an empty command.";
        }

        return await agentTools.ExecuteShellCommandAsync(
            plan.Command,
            plan.WorkingDirectory,
            plan.TimeoutSeconds <= 0 ? 60 : plan.TimeoutSeconds);
    }

    private static async Task<ShellCommandPlan> GenerateShellCommandPlanAsync(
        string goal,
        AgentTask task,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.ShellScriptSystemPrompt),
            new(
                ChatRole.User,
                Prompts.PromptLibrary.BuildShellScriptUserPrompt(
                    GetOperatingSystemName(),
                    Environment.CurrentDirectory,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    goal,
                    task.Argument,
                    observations.FormatObservations()))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress("Generating shell script..."))
        {
            response = await chatClient.GetResponseAsync(messages, CreateJsonChatOptions(0.0), cancellationToken);
        }

        string json = ExtractJsonObject(response.Text);
        ShellCommandPlan? plan = JsonSerializer.Deserialize<ShellCommandPlan>(json, JsonOptions);
        if (plan is null)
        {
            throw new InvalidOperationException("Shell-script model did not return valid JSON.");
        }

        plan.Command = plan.Command.Trim();
        if (!string.IsNullOrWhiteSpace(plan.WorkingDirectory))
        {
            plan.WorkingDirectory = plan.WorkingDirectory.Trim();
        }

        return plan;
    }

    private static ShellCommandPlan? TryUseConcreteTaskCommand(string argument)
    {
        string command = argument.Trim();
        if (string.IsNullOrWhiteSpace(command) ||
            ContainsShellControlOperator(command) ||
            !StartsWithKnownShellCommand(command))
        {
            return null;
        }

        return new ShellCommandPlan
        {
            Command = command,
            WorkingDirectory = null,
            TimeoutSeconds = 60
        };
    }

    private static bool StartsWithKnownShellCommand(string command)
    {
        string executable = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        executable = Path.GetFileName(executable);

        return executable.Equals("ls", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("dir", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("mkdir", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("pnpm", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("yarn", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("git", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("cargo", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("go", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("make", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("cmake", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("msbuild", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("python", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("python3", StringComparison.OrdinalIgnoreCase) ||
               executable.Equals("node", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsShellControlOperator(string command)
    {
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool escaped = false;

        for (int i = 0; i < command.Length; i++)
        {
            char current = command[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current == '\\')
            {
                escaped = true;
                continue;
            }

            if (current == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (current == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                continue;
            }

            if (inSingleQuote || inDoubleQuote)
            {
                continue;
            }

            if (current is '\r' or '\n' or ';' or '|')
            {
                return true;
            }

            if (current == '&' && i + 1 < command.Length && command[i + 1] == '&')
            {
                return true;
            }

            if (current == '>' || current == '<')
            {
                return true;
            }
        }

        return false;
    }

    private static string GetOperatingSystemName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "macOS"
                : "Linux";

    private static string ExtractJsonObject(string text)
    {
        string trimmed = StringHelper.StripCodeFence(text).Trim();
        int start = trimmed.IndexOf('{', StringComparison.Ordinal);
        int end = trimmed.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("Model did not return a JSON object.");
        }

        return trimmed[start..(end + 1)];
    }
}
