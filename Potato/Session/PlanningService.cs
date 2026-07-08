using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Potato.Session.extensions;
using Potato.Session.Tasks;

namespace Potato.Session;

public class PlanningService(AgentTools agentTools)
{
    public async Task<List<AgentTask>> PlanAsync(string goal, IChatClient chatClient, CancellationToken cancellationToken)
    {
        string workspaceContext = await BuildProjectMapAsync(Environment.CurrentDirectory, chatClient, cancellationToken);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are Potato's deterministic planner. Return valid JSON only."),
            new(ChatRole.User, PromptLibrary.BuildPlannerUserPrompt(goal, workspaceContext))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress("Planning deterministic task list..."))
        {
            response = await chatClient.GetResponseAsync(messages, AgentTaskBase.CreateJsonChatOptions(0.0),
                cancellationToken);
        }

        string json = ExtractJsonArray(response.Text);
        List<AgentTask>? tasks = JsonSerializer.Deserialize<List<AgentTask>>(json, AgentTaskBase.JsonOptions);
        if (tasks is null || tasks.Count == 0)
        {
            throw new InvalidOperationException("Planner returned no tasks.");
        }

        ValidateTasks(tasks);
        return tasks.OrderBy(task => task.Step).ToList();
    }

    private async Task<string> BuildProjectMapAsync(string targetDirectory, IChatClient chatClient, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"ProjectMap root: {targetDirectory}");

        string[] files = Directory.GetFiles(targetDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsSkippedProjectMapPath(file))
            .OrderBy(file => Path.GetRelativePath(targetDirectory, file), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using (PotatoConsole.StartProgress($"Indexing {files.Length} C# files into ProjectMap..."))
        {
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = Path.GetRelativePath(targetDirectory, file);
                string content = await File.ReadAllTextAsync(file, cancellationToken);
                string summary = await SummarizeProjectFileAsync(relativePath, content, chatClient, cancellationToken);

                builder.AppendLine();
                builder.AppendLine($"File: {relativePath}");
                builder.AppendLine(summary);
            }
        }

        return builder.ToString();
    }
    
    private static bool IsSkippedProjectMapPath(string filePath)
    {
        string normalized = filePath.Replace("\\", "/", StringComparison.Ordinal);
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
    }
    
    private async Task<string> SummarizePathAsync(string path)
    {
        string? resolvedPath = ResolveLocalPath(path);
        if (resolvedPath is not null && Directory.Exists(resolvedPath))
        {
            return InspectDirectory(resolvedPath);
        }

        return await agentTools.SummarizeFilePurpose(path);
    }
    
    private string InspectDirectory(string directoryPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Directory inspection: {directoryPath}");
        builder.AppendLine();
        builder.AppendLine(agentTools.ListFiles(directoryPath, recursive: false, maxEntries: 300));
        builder.AppendLine();
        builder.AppendLine("Project manifests under this directory:");
        builder.AppendLine(agentTools.ListProjectFiles(directoryPath));
        return builder.ToString();
    }

    private static string? ResolveExistingDirectory(string? path)
    {
        string? resolvedPath = ResolveLocalPath(path);
        return resolvedPath is not null && Directory.Exists(resolvedPath) ? resolvedPath : null;
    }

    private static string? ResolveLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string trimmed = path.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) && uri.IsFile)
        {
            trimmed = uri.LocalPath;
        }

        if (trimmed.StartsWith("~/", StringComparison.Ordinal))
        {
            trimmed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), trimmed[2..]);
        }

        return Path.GetFullPath(Path.IsPathRooted(trimmed)
            ? trimmed
            : Path.Combine(Environment.CurrentDirectory, trimmed));
    }

    private static void ValidateTasks(IEnumerable<AgentTask> tasks)
    {
        int expectedStep = 1;
        foreach (AgentTask task in tasks.OrderBy(task => task.Step))
        {
            if (task.Step != expectedStep)
            {
                throw new InvalidOperationException(
                    $"Planner step numbers must be sequential. Expected {expectedStep}, got {task.Step}.");
            }

            if (string.IsNullOrWhiteSpace(task.Action))
            {
                throw new InvalidOperationException($"Planner step {task.Step} has no action.");
            }

            if (string.IsNullOrWhiteSpace(task.Argument))
            {
                throw new InvalidOperationException($"Planner step {task.Step} has no argument.");
            }

            string action = StringHelper.NormalizeAction(task.Action);
            if (action is not ("read" or "refactor-prompt" or "write-report"))
            {
                throw new InvalidOperationException(
                    $"Planner step {task.Step} has unsupported action '{task.Action}'.");
            }

            if (string.IsNullOrWhiteSpace(task.Reason))
            {
                throw new InvalidOperationException($"Planner step {task.Step} has no reason.");
            }

            expectedStep++;
        }
    }

    private static string ExtractJsonArray(string text)
    {
        string trimmed = StringHelper.StripCodeFence(text).Trim();
        int start = trimmed.IndexOf('[', StringComparison.Ordinal);
        int end = trimmed.LastIndexOf(']');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("Planner did not return a JSON array.");
        }

        return trimmed[start..(end + 1)];
    }
    
    private async Task<string> SummarizeProjectFileAsync(
        string filePath,
        string fileContent,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You summarize C# files for a repository map. Return concise bullets only."),
            new(ChatRole.User, PromptLibrary.BuildProjectMapUserPrompt(filePath, fileContent))
        };

        ChatResponse response = await chatClient.GetResponseAsync(
            messages, AgentTaskBase.CreateChatOptions(0.0),
            cancellationToken);

        return string.IsNullOrWhiteSpace(response.Text)
            ? "- No summary returned."
            : response.Text.Trim();
    }
}
