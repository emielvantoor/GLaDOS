using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Tasks;

namespace Potato.Session;

public class PlanningService(IEnumerable<IAgentTask> agentTasks)
{
    private const int ProjectMapCacheSchemaVersion = 1;
    private const string ProjectMapCacheDirectoryName = ".potato";
    private const string ProjectMapCacheFileName = "project-map-cache.json";

    private static readonly string[] ProjectMapFileNames =
    [
        "README",
        "README.md",
        "AGENTS.md",
        "agents.md",
        "FEATURE.md",
        "CONTRIBUTING.md",
        "copilot-instructions.md",
        "instructions.md",
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

        tasks = EnsureInstructionReadsBeforeImplementation(tasks, ExtractIndexedPaths(workspaceContext));
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

    public Task<string> BuildProjectMapAsync(
        string targetDirectory,
        IChatClient chatClient,
        CancellationToken cancellationToken) =>
        BuildProjectMapCoreAsync(targetDirectory, chatClient, cancellationToken);

    private async Task<string> BuildProjectMapCoreAsync(string targetDirectory, IChatClient chatClient, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"ProjectMap root: {targetDirectory}");

        FileInfo[] files = EnumerateProjectMapFiles(targetDirectory)
            .Where(IsProjectMapFile)
            .OrderBy(file => ToRelativeProjectMapPath(targetDirectory, file.FullName), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string promptHash = ComputeHash(Prompts.PromptLibrary.BuildProjectMapCacheKey);
        string cachePath = GetProjectMapCachePath(targetDirectory);
        ProjectMapCache cache = LoadProjectMapCache(cachePath);
        var currentRelativePaths = files
            .Select(file => ToRelativeProjectMapPath(targetDirectory, file.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (PruneDeletedProjectMapEntries(cache, currentRelativePaths))
        {
            SaveProjectMapCache(cachePath, cache);
        }

        using (PotatoConsole.IProgressReporter progress =
               PotatoConsole.StartProgress($"Building ProjectMap for {files.Length} project files..."))
        {
            for (int index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo file = files[index];
                string relativePath = ToRelativeProjectMapPath(targetDirectory, file.FullName);
                string fileHash = await ComputeFileHashAsync(file.FullName, cancellationToken);
                ProjectMapCacheEntry? cachedEntry = GetValidProjectMapCacheEntry(cache, relativePath, fileHash, promptHash);
                if (cachedEntry is not null)
                {
                    progress.Update(BuildProjectMapProgressMessage(index + 1, files.Length, relativePath, cached: true));
                    if (UpdateProjectMapCacheEntry(cache, relativePath, file, fileHash, promptHash, cachedEntry.Summary))
                    {
                        SaveProjectMapCache(cachePath, cache);
                    }

                    AppendProjectMapSummary(builder, relativePath, cachedEntry.Summary);
                    continue;
                }

                progress.Update(BuildProjectMapProgressMessage(index + 1, files.Length, relativePath, cached: false));
                string content = await File.ReadAllTextAsync(file.FullName, cancellationToken);
                string summary = await SummarizeProjectFileAsync(relativePath, content, chatClient, cancellationToken);

                cache.Entries[relativePath] = new ProjectMapCacheEntry
                {
                    LastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks,
                    Length = file.Length,
                    FileHash = fileHash,
                    PromptHash = promptHash,
                    Summary = summary
                };
                SaveProjectMapCache(cachePath, cache);
                AppendProjectMapSummary(builder, relativePath, summary);
            }
        }

        return builder.ToString();
    }

    private static string BuildProjectMapProgressMessage(int currentFile, int totalFiles, string relativePath, bool cached)
    {
        int percentage = totalFiles == 0
            ? 100
            : (int)Math.Round(currentFile * 100.0 / totalFiles);

        int fileNumberWidth = Math.Max(1, totalFiles.ToString().Length);
        string current = currentFile.ToString().PadLeft(fileNumberWidth);
        string source = cached ? "cached  " : "indexing";
        return $"Building ProjectMap {current}/{totalFiles} ({percentage,3}%, {source}): {relativePath}";
    }

    private static void AppendProjectMapSummary(StringBuilder builder, string relativePath, string summary)
    {
        builder.AppendLine();
        builder.AppendLine($"File: {relativePath}");
        builder.AppendLine(summary);
    }

    private static bool IsProjectMapFile(FileInfo file)
    {
        if (IsSkippedProjectMapPath(file.FullName))
        {
            return false;
        }

        string fileName = file.Name;
        if (SkippedProjectMapFileNames.Any(name => fileName.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            fileName.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ProjectMapFileNames.Any(name => fileName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string extension = file.Extension;
        return ProjectMapExtensions.Any(knownExtension =>
            extension.Equals(knownExtension, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<FileInfo> EnumerateProjectMapFiles(string directoryPath)
    {
        var root = new DirectoryInfo(directoryPath);
        if (!root.Exists)
        {
            return [];
        }

        return EnumerateProjectMapFiles(root);
    }

    private static IEnumerable<FileInfo> EnumerateProjectMapFiles(DirectoryInfo directory)
    {
        foreach (FileInfo file in directory.EnumerateFiles())
        {
            yield return file;
        }

        foreach (DirectoryInfo childDirectory in directory.EnumerateDirectories())
        {
            if (ShouldSkipProjectMapDirectory(childDirectory))
            {
                continue;
            }

            foreach (FileInfo file in EnumerateProjectMapFiles(childDirectory))
            {
                yield return file;
            }
        }
    }

    private static bool ShouldSkipProjectMapDirectory(DirectoryInfo directory)
    {
        if (directory.Name.Equals(".github", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return directory.Name.StartsWith(".", StringComparison.Ordinal) ||
               directory.Attributes.HasFlag(FileAttributes.Hidden) ||
               directory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
               directory.Name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
               directory.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
               directory.Name.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
               directory.Name.Equals("build", StringComparison.OrdinalIgnoreCase) ||
               directory.Name.Equals("coverage", StringComparison.OrdinalIgnoreCase) ||
               directory.Name.Equals("vendor", StringComparison.OrdinalIgnoreCase);
    }
    
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

    private static ProjectMapCacheEntry? GetValidProjectMapCacheEntry(
        ProjectMapCache cache,
        string relativePath,
        string fileHash,
        string promptHash)
    {
        if (!cache.Entries.TryGetValue(relativePath, out ProjectMapCacheEntry? entry))
        {
            return null;
        }

        return string.Equals(entry.FileHash, fileHash, StringComparison.Ordinal) &&
               string.Equals(entry.PromptHash, promptHash, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(entry.Summary)
            ? entry
            : null;
    }

    private static bool UpdateProjectMapCacheEntry(
        ProjectMapCache cache,
        string relativePath,
        FileInfo file,
        string fileHash,
        string promptHash,
        string summary)
    {
        if (cache.Entries.TryGetValue(relativePath, out ProjectMapCacheEntry? entry) &&
            entry.LastWriteTimeUtcTicks == file.LastWriteTimeUtc.Ticks &&
            entry.Length == file.Length &&
            string.Equals(entry.FileHash, fileHash, StringComparison.Ordinal) &&
            string.Equals(entry.PromptHash, promptHash, StringComparison.Ordinal) &&
            string.Equals(entry.Summary, summary, StringComparison.Ordinal))
        {
            return false;
        }

        cache.Entries[relativePath] = new ProjectMapCacheEntry
        {
            LastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks,
            Length = file.Length,
            FileHash = fileHash,
            PromptHash = promptHash,
            Summary = summary
        };
        return true;
    }

    private static ProjectMapCache LoadProjectMapCache(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath))
            {
                return new ProjectMapCache();
            }

            string json = File.ReadAllText(cachePath);
            ProjectMapCache? cache = JsonSerializer.Deserialize<ProjectMapCache>(json, ProjectMapCacheJsonOptions);
            if (cache is null || cache.SchemaVersion != ProjectMapCacheSchemaVersion)
            {
                return new ProjectMapCache();
            }

            return cache;
        }
        catch
        {
            return new ProjectMapCache();
        }
    }

    private static void SaveProjectMapCache(string cachePath, ProjectMapCache cache)
    {
        string? cacheDirectory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(cacheDirectory))
        {
            Directory.CreateDirectory(cacheDirectory);
        }

        string json = JsonSerializer.Serialize(cache, ProjectMapCacheJsonOptions);
        File.WriteAllText(cachePath, json);
    }

    private static bool PruneDeletedProjectMapEntries(ProjectMapCache cache, IReadOnlySet<string> currentRelativePaths)
    {
        string[] removedPaths = cache.Entries.Keys
            .Where(path => !currentRelativePaths.Contains(path))
            .ToArray();

        foreach (string removedPath in removedPaths)
        {
            cache.Entries.Remove(removedPath);
        }

        return removedPaths.Length > 0;
    }

    private static string GetProjectMapCachePath(string targetDirectory) =>
        Path.Combine(targetDirectory, ProjectMapCacheDirectoryName, ProjectMapCacheFileName);

    private static string ToRelativeProjectMapPath(string targetDirectory, string filePath) =>
        Path.GetRelativePath(targetDirectory, filePath).Replace('\\', '/');

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static List<AgentTask> EnsureInstructionReadsBeforeImplementation(
        IReadOnlyList<AgentTask> tasks,
        IReadOnlySet<string> indexedPaths)
    {
        int firstImplementationIndex = FindFirstImplementationIndex(tasks);
        if (firstImplementationIndex < 0)
        {
            return tasks.OrderBy(task => task.Step).ToList();
        }

        string[] instructionReads = FindRelevantInstructionFiles(tasks, indexedPaths)
            .Where(path => !tasks.Any(task =>
                StringHelper.NormalizeAction(task.Action) == "read" &&
                string.Equals(NormalizeProjectPath(task.Argument), path, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (instructionReads.Length == 0)
        {
            return tasks.OrderBy(task => task.Step).ToList();
        }

        var result = new List<AgentTask>();
        foreach (AgentTask task in tasks.OrderBy(task => task.Step))
        {
            if (result.Count == firstImplementationIndex)
            {
                result.AddRange(instructionReads.Select(path => new AgentTask
                {
                    Action = "read",
                    Argument = path,
                    Reason = "Read relevant project instruction or feature guidance before implementation."
                }));
            }

            result.Add(task);
        }

        return RenumberTasks(result);
    }

    private static int FindFirstImplementationIndex(IReadOnlyList<AgentTask> tasks)
    {
        AgentTask[] orderedTasks = tasks.OrderBy(task => task.Step).ToArray();
        for (int index = 0; index < orderedTasks.Length; index++)
        {
            string action = StringHelper.NormalizeAction(orderedTasks[index].Action);
            if (action is "create-file" or "apply-patch")
            {
                return index;
            }
        }

        return -1;
    }

    private static IReadOnlySet<string> ExtractIndexedPaths(string workspaceContext)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in workspaceContext.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            const string prefix = "File: ";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                paths.Add(NormalizeProjectPath(line[prefix.Length..]));
            }
        }

        return paths;
    }

    private static IEnumerable<string> FindRelevantInstructionFiles(
        IReadOnlyList<AgentTask> tasks,
        IReadOnlySet<string> indexedPaths)
    {
        var candidates = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in indexedPaths.Where(IsGlobalInstructionPath))
        {
            candidates.Add(path);
        }

        foreach (string targetPath in ExtractImplementationTargetPaths(tasks))
        {
            foreach (string directory in EnumerateAncestorDirectories(targetPath))
            {
                foreach (string fileName in InstructionFileNames)
                {
                    string candidate = string.IsNullOrEmpty(directory) ? fileName : $"{directory}/{fileName}";
                    if (indexedPaths.Contains(candidate))
                    {
                        candidates.Add(candidate);
                    }
                }
            }
        }

        return candidates;
    }

    private static IEnumerable<string> ExtractImplementationTargetPaths(IEnumerable<AgentTask> tasks)
    {
        foreach (AgentTask task in tasks)
        {
            string action = StringHelper.NormalizeAction(task.Action);
            if (action == "create-file")
            {
                string path = NormalizeProjectPath(task.Argument);
                if (LooksLikeProjectPath(path))
                {
                    yield return path;
                }
            }
            else if (action == "apply-patch" &&
                     TryExtractTargetFile(task.Argument, out string? targetFilePath) &&
                     targetFilePath is not null)
            {
                yield return NormalizeProjectPath(targetFilePath);
            }
        }
    }

    private static IEnumerable<string> EnumerateAncestorDirectories(string path)
    {
        string? directory = Path.GetDirectoryName(NormalizeProjectPath(path))?.Replace('\\', '/');
        while (directory is not null)
        {
            yield return directory == "." ? string.Empty : directory;
            if (string.IsNullOrEmpty(directory))
            {
                yield break;
            }

            directory = Path.GetDirectoryName(directory)?.Replace('\\', '/');
        }
    }

    private static bool IsGlobalInstructionPath(string path) =>
        string.Equals(path, "AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, "agents.md", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, ".github/copilot-instructions.md", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, ".github/instructions.md", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(".github/features/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] InstructionFileNames =
    [
        "AGENTS.md",
        "agents.md",
        "FEATURE.md",
        "README.md",
        "CONTRIBUTING.md",
        "copilot-instructions.md",
        "instructions.md"
    ];

    private static bool TryExtractTargetFile(string argument, out string? targetFilePath)
    {
        targetFilePath = null;
        const string targetPrefix = "Target file:";
        string normalized = argument.Replace("\r\n", "\n", StringComparison.Ordinal);
        int targetIndex = normalized.IndexOf(targetPrefix, StringComparison.OrdinalIgnoreCase);
        if (targetIndex < 0)
        {
            return false;
        }

        int pathStart = targetIndex + targetPrefix.Length;
        int pathEnd = normalized.IndexOf('\n', pathStart);
        string path = (pathEnd < 0 ? normalized[pathStart..] : normalized[pathStart..pathEnd]).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        targetFilePath = path;
        return true;
    }

    private static bool LooksLikeProjectPath(string value)
    {
        string path = NormalizeProjectPath(value);
        return path.Contains('/', StringComparison.Ordinal) || Path.HasExtension(path);
    }

    private static string NormalizeProjectPath(string path)
    {
        string normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized))
        {
            normalized = Path.GetRelativePath(Environment.CurrentDirectory, normalized).Replace('\\', '/');
        }

        return normalized.TrimStart('/');
    }

    private static List<AgentTask> RenumberTasks(IEnumerable<AgentTask> tasks) =>
        tasks.Select((task, index) => task with { Step = index + 1 }).ToList();
    
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

    private static readonly JsonSerializerOptions ProjectMapCacheJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class ProjectMapCache
    {
        public int SchemaVersion { get; init; } = ProjectMapCacheSchemaVersion;

        public Dictionary<string, ProjectMapCacheEntry> Entries { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProjectMapCacheEntry
    {
        public long LastWriteTimeUtcTicks { get; init; }

        public long Length { get; init; }

        public string FileHash { get; init; } = string.Empty;

        public string PromptHash { get; init; } = string.Empty;

        public string Summary { get; init; } = string.Empty;
    }
}
