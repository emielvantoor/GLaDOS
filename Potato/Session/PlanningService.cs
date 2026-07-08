using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Tasks;

namespace Potato.Session;

public class PlanningService(IEnumerable<IAgentTask> agentTasks)
{
    private static readonly string[] ProjectMapFileNames =
    [
        "README",
        "README.md",
        "package.json",
        "tsconfig.json",
        "vite.config.js",
        "vite.config.ts",
        "next.config.js",
        "next.config.ts",
        "pyproject.toml",
        "requirements.txt",
        "Cargo.toml",
        "go.mod",
        "pom.xml",
        "build.gradle",
        "settings.gradle",
        "Dockerfile"
    ];

    private static readonly string[] SkippedProjectMapFileNames =
    [
        "package-lock.json",
        "pnpm-lock.yaml",
        "yarn.lock",
        "composer.lock",
        "Cargo.lock"
    ];

    private static readonly string[] ProjectMapExtensions =
    [
        ".cs",
        ".fs",
        ".vb",
        ".csproj",
        ".fsproj",
        ".vbproj",
        ".sln",
        ".ts",
        ".tsx",
        ".js",
        ".jsx",
        ".mjs",
        ".cjs",
        ".vue",
        ".svelte",
        ".html",
        ".css",
        ".scss",
        ".py",
        ".java",
        ".kt",
        ".kts",
        ".go",
        ".rs",
        ".php",
        ".rb",
        ".swift",
        ".c",
        ".h",
        ".cpp",
        ".hpp",
        ".json",
        ".yaml",
        ".yml",
        ".toml",
        ".xml",
        ".gradle",
        ".md"
    ];

    public async Task<List<AgentTask>> PlanAsync(string goal, IChatClient chatClient, CancellationToken cancellationToken)
    {
        string workspaceContext = await BuildProjectMapAsync(Environment.CurrentDirectory, chatClient, cancellationToken);
        IReadOnlyList<string> supportedActions = GetSupportedActions();
        IReadOnlyList<string> planningGuidance = GetPlanningGuidance();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.PlannerSystemPrompt),
            new(ChatRole.User, Prompts.PromptLibrary.BuildPlannerUserPrompt(
                goal,
                workspaceContext,
                supportedActions,
                planningGuidance))
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

        ValidateTasks(tasks, supportedActions);
        return tasks.OrderBy(task => task.Step).ToList();
    }

    private IReadOnlyList<string> GetSupportedActions() =>
        agentTasks
            .Select(task => task.ActionName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(action => action, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private IReadOnlyList<string> GetPlanningGuidance() =>
        agentTasks
            .OrderBy(task => task.ActionName, StringComparer.OrdinalIgnoreCase)
            .SelectMany(task => task.PlanningGuidance)
            .Where(guidance => !string.IsNullOrWhiteSpace(guidance))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private async Task<string> BuildProjectMapAsync(string targetDirectory, IChatClient chatClient, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"ProjectMap root: {targetDirectory}");

        string[] files = EnumerateProjectMapFiles(targetDirectory)
            .Where(IsProjectMapFile)
            .OrderBy(file => Path.GetRelativePath(targetDirectory, file), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using (PotatoConsole.IProgressReporter progress =
               PotatoConsole.StartProgress($"Indexing {files.Length} project files into ProjectMap..."))
        {
            for (int index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string file = files[index];
                string relativePath = Path.GetRelativePath(targetDirectory, file);
                progress.Update(BuildProjectMapProgressMessage(index + 1, files.Length, relativePath));

                string content = await File.ReadAllTextAsync(file, cancellationToken);
                string summary = await SummarizeProjectFileAsync(relativePath, content, chatClient, cancellationToken);

                builder.AppendLine();
                builder.AppendLine($"File: {relativePath}");
                builder.AppendLine(summary);
            }
        }

        return builder.ToString();
    }

    private static string BuildProjectMapProgressMessage(int currentFile, int totalFiles, string relativePath)
    {
        int percentage = totalFiles == 0
            ? 100
            : (int)Math.Round(currentFile * 100.0 / totalFiles);

        return $"Indexing ProjectMap {currentFile}/{totalFiles} ({percentage}%): {relativePath}";
    }

    private static bool IsProjectMapFile(string filePath)
    {
        if (IsSkippedProjectMapPath(filePath))
        {
            return false;
        }

        string fileName = Path.GetFileName(filePath);
        if (SkippedProjectMapFileNames.Any(name => fileName.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            fileName.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ProjectMapFileNames.Any(name => fileName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string extension = Path.GetExtension(filePath);
        return ProjectMapExtensions.Any(knownExtension =>
            extension.Equals(knownExtension, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateProjectMapFiles(string directoryPath)
    {
        var root = new DirectoryInfo(directoryPath);
        if (!root.Exists)
        {
            return [];
        }

        return EnumerateProjectMapFiles(root);
    }

    private static IEnumerable<string> EnumerateProjectMapFiles(DirectoryInfo directory)
    {
        foreach (FileInfo file in directory.EnumerateFiles())
        {
            yield return file.FullName;
        }

        foreach (DirectoryInfo childDirectory in directory.EnumerateDirectories())
        {
            if (ShouldSkipProjectMapDirectory(childDirectory))
            {
                continue;
            }

            foreach (string file in EnumerateProjectMapFiles(childDirectory))
            {
                yield return file;
            }
        }
    }

    private static bool ShouldSkipProjectMapDirectory(DirectoryInfo directory) =>
        directory.Name.StartsWith(".", StringComparison.Ordinal) ||
        directory.Attributes.HasFlag(FileAttributes.Hidden) ||
        directory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
        directory.Name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
        directory.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
        directory.Name.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
        directory.Name.Equals("build", StringComparison.OrdinalIgnoreCase) ||
        directory.Name.Equals("coverage", StringComparison.OrdinalIgnoreCase) ||
        directory.Name.Equals("vendor", StringComparison.OrdinalIgnoreCase);
    
    private static bool IsSkippedProjectMapPath(string filePath)
    {
        string normalized = filePath.Replace("\\", "/", StringComparison.Ordinal);
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/dist/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/build/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/coverage/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.next/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/vendor/", StringComparison.OrdinalIgnoreCase);
    }
    
    private static void ValidateTasks(IEnumerable<AgentTask> tasks, IReadOnlyCollection<string> supportedActions)
    {
        var supportedActionSet = supportedActions.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            if (!supportedActionSet.Contains(action))
            {
                throw new InvalidOperationException(
                    $"Planner step {task.Step} has unsupported action '{task.Action}'. Supported actions: {string.Join(", ", supportedActions)}.");
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
            new(ChatRole.System, Prompts.PromptLibrary.BuildProjectMapSystemPrompt),
            new(ChatRole.User, Prompts.PromptLibrary.BuildProjectMapUserPrompt(filePath, fileContent))
        };

        ChatResponse response = await chatClient.GetResponseAsync(
            messages, AgentTaskBase.CreateChatOptions(0.0),
            cancellationToken);

        return string.IsNullOrWhiteSpace(response.Text)
            ? "- No summary returned."
            : response.Text.Trim();
    }
}
