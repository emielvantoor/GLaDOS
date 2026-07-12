using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Session.Tasks;

namespace Potato.Session;

public class PlanningService(IEnumerable<IAgentTask> agentTasks)
{
    private const int MaxPlannerValidationAttempts = 10;
    private const int MaxDraftPlanAttempts = 5;
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

    public Task<List<AgentTask>> PlanAsync(string goal, IChatClient chatClient, CancellationToken cancellationToken) =>
        PlanAsync(goal, [], chatClient, cancellationToken);

    public async Task<List<AgentTask>> PlanAsync(
        string goal,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        string workspaceContext = await BuildProjectMapAsync(Environment.CurrentDirectory, chatClient, cancellationToken);
        string workspaceFileIndex = BuildWorkspaceFileIndex(workspaceContext);
        string planningSpec = await GeneratePlanningSpecAsync(goal, workspaceFileIndex, chatClient, cancellationToken);
        string draftPlan = await GenerateApprovedDraftPlanAsync(goal, planningSpec, workspaceFileIndex, chatClient, cancellationToken);
        IReadOnlyList<string> supportedActions = GetSupportedActions();
        IReadOnlyList<string> planningGuidance = GetPlanningGuidance();
        string executionObservations = observations.FormatObservations();
        Exception? lastPlanningError = null;
        var plannerValidationMessages = new List<string>();

        for (int attempt = 1; attempt <= MaxPlannerValidationAttempts; attempt++)
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, Prompts.PromptLibrary.PlannerSystemPrompt),
                new(ChatRole.User, Prompts.PromptLibrary.BuildPlannerUserPrompt(
                    goal,
                    workspaceFileIndex,
                    planningSpec,
                    draftPlan,
                    supportedActions,
                    planningGuidance,
                    executionObservations))
            };

            ChatResponse response;
            using (PotatoConsole.StartProgress(FormatPlanningProgress(attempt)))
            {
                response = await chatClient.GetResponseAsync(messages, AgentTaskBase.CreateJsonChatOptions(0.0),
                    cancellationToken);
            }

            try
            {
                string json = ExtractJsonArray(response.Text);
                List<AgentTask>? tasks = JsonSerializer.Deserialize<List<AgentTask>>(json, AgentTaskBase.JsonOptions);
                if (tasks is null || tasks.Count == 0)
                {
                    throw new InvalidOperationException("Planner returned no tasks.");
                }

                IReadOnlySet<string> indexedPaths = ExtractIndexedPaths(workspaceContext);
                IReadOnlySet<string> availablePaths = AddObservedExistingPaths(indexedPaths, observations);
                tasks = PreferAttachedMentionPaths(tasks, goal, availablePaths);
                tasks = EnsureInstructionReadsBeforeImplementation(tasks, availablePaths);
                tasks = ResolveUniqueIndexedPathReferences(tasks, availablePaths);
                tasks = RewriteCreateFileForExistingDocumentation(tasks, availablePaths);
                tasks = RemoveRedundantEditsForCreatedSourceFiles(tasks);
                ValidateTasks(goal, tasks, supportedActions, availablePaths);
                // await ValidatePlanCompletenessAsync(goal, planningSpec, draftPlan, workspaceFileIndex, tasks, chatClient, cancellationToken);
                return tasks.OrderBy(task => task.Step).ToList();
            }
            catch (InvalidOperationException ex)
            {
                lastPlanningError = ex;
                plannerValidationMessages.Add(ex.Message);
                if (attempt == MaxPlannerValidationAttempts)
                {
                    break;
                }

                PotatoConsole.WriteStatus($"Planner validation failed: {ex.Message}");
                executionObservations = BuildPlannerRetryObservations(observations, plannerValidationMessages);
            }
        }

        throw new InvalidOperationException(
            $"Planner could not produce a valid task list after {MaxPlannerValidationAttempts} attempts: {lastPlanningError?.Message}");
    }

    private static string FormatPlanningProgress(int attempt) =>
        attempt == 1
            ? "Planning deterministic task list..."
            : $"Repairing planner task list ({attempt}/{MaxPlannerValidationAttempts})...";

    private static async Task<string> GeneratePlanningSpecAsync(
        string goal,
        string workspaceFileIndex,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.PlanningSpecSystemPrompt),
            new(ChatRole.User, Prompts.PromptLibrary.BuildPlanningSpecUserPrompt(goal, workspaceFileIndex))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress("Generating implementation spec..."))
        {
            response = await chatClient.GetResponseAsync(
                messages,
                AgentTaskBase.CreateJsonChatOptions(0.0),
                cancellationToken);
        }

        return ResolveSpecPathReferences(ExtractJsonObject(response.Text), workspaceFileIndex);
    }

    private static async Task<string> GenerateApprovedDraftPlanAsync(
        string goal,
        string planningSpec,
        string workspaceContext,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var draftFeedback = new List<string>();
        string latestDraftPlan = string.Empty;
        for (int attempt = 1; attempt <= MaxDraftPlanAttempts; attempt++)
        {
            latestDraftPlan = await GenerateDraftPlanAsync(
                goal,
                planningSpec,
                workspaceContext,
                FormatDraftFeedback(draftFeedback),
                chatClient,
                cancellationToken);

            PlanCompletenessReview review = await ReviewDraftPlanAsync(
                planningSpec,
                latestDraftPlan,
                chatClient,
                cancellationToken);
            if (review.IsComplete)
            {
                return latestDraftPlan;
            }

            string feedback = string.IsNullOrWhiteSpace(review.Feedback)
                ? "Draft plan does not satisfy the derived implementation spec."
                : review.Feedback.Trim();
            draftFeedback.Add(feedback);
            PotatoConsole.WriteStatus($"Draft plan review failed: {feedback}");
        }

        throw new InvalidOperationException(
            $"Planner could not produce a complete draft plan after {MaxDraftPlanAttempts} attempts: {draftFeedback.LastOrDefault() ?? latestDraftPlan}");
    }

    private static async Task<string> GenerateDraftPlanAsync(
        string goal,
        string planningSpec,
        string workspaceContext,
        string draftFeedback,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.DraftPlanSystemPrompt),
            new(
                ChatRole.User,
                Prompts.PromptLibrary.BuildDraftPlanUserPrompt(
                    goal,
                    planningSpec,
                    workspaceContext,
                    draftFeedback))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress("Drafting implementation plan..."))
        {
            response = await chatClient.GetResponseAsync(
                messages,
                AgentTaskBase.CreateJsonChatOptions(0.0),
                cancellationToken);
        }

        return ExtractJsonArray(response.Text);
    }

    private static string ResolveSpecPathReferences(string planningSpec, string workspaceContext)
    {
        IReadOnlySet<string> indexedPaths = ExtractIndexedPaths(workspaceContext);
        string resolvedSpec = planningSpec;
        foreach (string fileName in ExtractLikelyFileNames(planningSpec).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryResolveUniqueIndexedBasename(fileName, indexedPaths, out string resolvedPath))
            {
                resolvedSpec = Regex.Replace(
                    resolvedSpec,
                    $@"(?<![\w./-]){Regex.Escape(fileName)}(?![\w./-])",
                    resolvedPath,
                    RegexOptions.IgnoreCase);
            }
        }

        return resolvedSpec;
    }

    private static IEnumerable<string> ExtractLikelyFileNames(string text)
    {
        foreach (Match match in Regex.Matches(
                     text,
                     @"(?<![\w./-])(?<file>[\w.-]+\.[A-Za-z0-9]+)(?![\w./-])"))
        {
            yield return match.Groups["file"].Value;
        }
    }

    private static async Task<PlanCompletenessReview> ReviewDraftPlanAsync(
        string planningSpec,
        string draftPlan,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.DraftPlanReviewSystemPrompt),
            new(ChatRole.User, Prompts.PromptLibrary.BuildDraftPlanReviewUserPrompt(planningSpec, draftPlan))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress("Reviewing draft plan..."))
        {
            response = await chatClient.GetResponseAsync(
                messages,
                AgentTaskBase.CreateJsonChatOptions(0.0),
                cancellationToken);
        }

        return ParsePlanCompletenessReview(response.Text, "Draft plan reviewer returned no review.");
    }

    private static string FormatDraftFeedback(IReadOnlyList<string> draftFeedback)
    {
        if (draftFeedback.Count == 0)
        {
            return "(none)";
        }

        var builder = new StringBuilder();
        for (int index = 0; index < draftFeedback.Count; index++)
        {
            builder.AppendLine($"{index + 1}. {draftFeedback[index]}");
        }

        return builder.ToString();
    }

    private static string BuildWorkspaceFileIndex(string workspaceContext)
    {
        var builder = new StringBuilder();
        foreach (string line in workspaceContext.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("ProjectMap root:", StringComparison.Ordinal) ||
                line.StartsWith("File: ", StringComparison.Ordinal))
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString();
    }

    private static string BuildPlannerRetryObservations(
        IReadOnlyList<TaskObservation> observations,
        IReadOnlyList<string> validationMessages)
    {
        var builder = new StringBuilder();
        string formattedObservations = observations.FormatObservations();
        if (!string.Equals(formattedObservations, "(none)", StringComparison.Ordinal))
        {
            builder.AppendLine(formattedObservations.TrimEnd());
            builder.AppendLine();
        }

        builder.AppendLine("Planner validation feedback from previous attempts:");
        foreach ((string validationMessage, int index) in validationMessages.Select((message, index) => (message, index)))
        {
            builder.AppendLine($"{index + 1}. {validationMessage}");
        }

        builder.AppendLine("Return a corrected JSON array that completes the user request and satisfies all validation feedback above.");
        return builder.ToString();
    }

    private static async Task ValidatePlanCompletenessAsync(
        string goal,
        string planningSpec,
        string draftPlan,
        string workspaceContext,
        IReadOnlyList<AgentTask> tasks,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.PlanCompletenessReviewSystemPrompt),
            new(
                ChatRole.User,
                Prompts.PromptLibrary.BuildPlanCompletenessReviewUserPrompt(
                    goal,
                    planningSpec,
                    draftPlan,
                    workspaceContext,
                    FormatPlanForReview(tasks)))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress("Reviewing plan completeness..."))
        {
            response = await chatClient.GetResponseAsync(
                messages,
                AgentTaskBase.CreateJsonChatOptions(0.0),
                cancellationToken);
        }

        PlanCompletenessReview review = ParsePlanCompletenessReview(
            response.Text,
            "Plan completeness reviewer returned no review.");
        if (!review.IsComplete)
        {
            string feedback = string.IsNullOrWhiteSpace(review.Feedback)
                ? "Plan completeness reviewer said the plan does not complete the user request."
                : review.Feedback.Trim();
            throw new InvalidOperationException($"Plan does not complete the user request: {feedback}");
        }
    }

    private static string FormatPlanForReview(IEnumerable<AgentTask> tasks)
    {
        var builder = new StringBuilder();
        foreach (AgentTask task in tasks.OrderBy(task => task.Step))
        {
            builder.AppendLine($"{task.Step}. {task.Action}: {task.Argument}");
            builder.AppendLine($"   Reason: {task.Reason}");
        }

        return builder.ToString();
    }

    private static PlanCompletenessReview ParsePlanCompletenessReview(string text, string nullReviewMessage)
    {
        string json = ExtractJsonObject(text);
        PlanCompletenessReview? review = JsonSerializer.Deserialize<PlanCompletenessReview>(
            json,
            AgentTaskBase.JsonOptions);
        if (review is null)
        {
            throw new InvalidOperationException(nullReviewMessage);
        }

        return review;
    }

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
        ProjectMapCacheLocation cacheLocation = GetProjectMapCacheLocation(targetDirectory);
        var builder = new StringBuilder();
        builder.AppendLine($"ProjectMap root: {cacheLocation.TargetDirectory}");

        FileInfo[] files = EnumerateProjectMapFiles(cacheLocation.TargetDirectory)
            .Where(IsProjectMapFile)
            .OrderBy(file => ToRelativeProjectMapPath(cacheLocation.TargetDirectory, file.FullName), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string promptHash = ComputeHash(Prompts.PromptLibrary.BuildProjectMapCacheKey);
        ProjectMapCache cache = LoadProjectMapCache(cacheLocation.CachePath);
        var currentRelativePaths = files
            .Select(file => ToRelativeProjectMapPath(cacheLocation.CacheRootDirectory, file.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (PruneDeletedProjectMapEntries(cache, currentRelativePaths, cacheLocation.CachePrunePathPrefix))
        {
            SaveProjectMapCache(cacheLocation.CachePath, cache);
        }

        using (PotatoConsole.IProgressReporter progress =
               PotatoConsole.StartProgress($"Building ProjectMap for {files.Length} project files..."))
        {
            for (int index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo file = files[index];
                string relativePath = ToRelativeProjectMapPath(cacheLocation.TargetDirectory, file.FullName);
                string cacheRelativePath = ToRelativeProjectMapPath(cacheLocation.CacheRootDirectory, file.FullName);
                string fileHash = await ComputeFileHashAsync(file.FullName, cancellationToken);
                ProjectMapCacheEntry? cachedEntry = GetValidProjectMapCacheEntry(cache, cacheRelativePath, fileHash, promptHash);
                if (cachedEntry is not null)
                {
                    progress.Update(BuildProjectMapProgressMessage(index + 1, files.Length, relativePath, cached: true));
                    if (UpdateProjectMapCacheEntry(cache, cacheRelativePath, file, fileHash, promptHash, cachedEntry.Summary))
                    {
                        SaveProjectMapCache(cacheLocation.CachePath, cache);
                    }

                    AppendProjectMapSummary(builder, relativePath, cachedEntry.Summary);
                    continue;
                }

                progress.Update(BuildProjectMapProgressMessage(index + 1, files.Length, relativePath, cached: false));
                string content = await File.ReadAllTextAsync(file.FullName, cancellationToken);
                string summary = await SummarizeProjectFileAsync(relativePath, content, chatClient, cancellationToken);

                cache.Entries[cacheRelativePath] = new ProjectMapCacheEntry
                {
                    LastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks,
                    Length = file.Length,
                    FileHash = fileHash,
                    PromptHash = promptHash,
                    Summary = summary
                };
                SaveProjectMapCache(cacheLocation.CachePath, cache);
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
        return ContainsPathSegment(normalized, "bin") ||
               ContainsPathSegment(normalized, "obj") ||
               ContainsPathSegment(normalized, ".git") ||
               ContainsPathSegment(normalized, "node_modules") ||
               ContainsPathSegment(normalized, "dist") ||
               ContainsPathSegment(normalized, "build") ||
               ContainsPathSegment(normalized, "coverage") ||
               ContainsPathSegment(normalized, ".next") ||
               ContainsPathSegment(normalized, "vendor");
    }

    private static bool ContainsPathSegment(string path, string segment) =>
        path.Equals(segment, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith($"{segment}/", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith($"/{segment}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"/{segment}/", StringComparison.OrdinalIgnoreCase);

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

    private static bool PruneDeletedProjectMapEntries(
        ProjectMapCache cache,
        IReadOnlySet<string> currentRelativePaths,
        string? scopedPathPrefix)
    {
        string[] removedPaths = cache.Entries.Keys
            .Where(path => IsPathInCachePruneScope(path, scopedPathPrefix) && !currentRelativePaths.Contains(path))
            .ToArray();

        foreach (string removedPath in removedPaths)
        {
            cache.Entries.Remove(removedPath);
        }

        return removedPaths.Length > 0;
    }

    private static bool IsPathInCachePruneScope(string cacheRelativePath, string? scopedPathPrefix) =>
        string.IsNullOrEmpty(scopedPathPrefix) ||
        cacheRelativePath.Equals(scopedPathPrefix, StringComparison.OrdinalIgnoreCase) ||
        cacheRelativePath.StartsWith(scopedPathPrefix + '/', StringComparison.OrdinalIgnoreCase);

    private static ProjectMapCacheLocation GetProjectMapCacheLocation(string targetDirectory)
    {
        string fullTargetDirectory = Path.GetFullPath(targetDirectory);
        string cacheRootDirectory = FindGitRepositoryRoot(fullTargetDirectory) ?? fullTargetDirectory;
        string projectMapRootDirectory = cacheRootDirectory;
        string cachePrunePathPrefix = ToRelativeProjectMapPath(cacheRootDirectory, projectMapRootDirectory);
        return new ProjectMapCacheLocation(
            projectMapRootDirectory,
            cacheRootDirectory,
            Path.Combine(cacheRootDirectory, ProjectMapCacheDirectoryName, ProjectMapCacheFileName),
            cachePrunePathPrefix == "." ? null : cachePrunePathPrefix);
    }

    private static string? FindGitRepositoryRoot(string directoryPath)
    {
        var directory = new DirectoryInfo(directoryPath);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

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
            if (IsImplementationAction(action))
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
            else if ((action == "apply-patch" || action == "write-code") &&
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

    private static bool IsImplementationAction(string action) =>
        action is "apply-patch" or "write-code" or "create-file" or "write-documentation";

    private static bool IsContextGatheringAction(string action) =>
        action is "read" or "inspect-project" or "search-files" or "search-file-contents" or
            "list-files" or "list-project-files" or "summarize-file-purpose";

    private static IReadOnlySet<string> AddObservedExistingPaths(
        IReadOnlySet<string> indexedPaths,
        IReadOnlyList<TaskObservation> observations)
    {
        var availablePaths = new HashSet<string>(indexedPaths, StringComparer.OrdinalIgnoreCase);
        foreach (TaskObservation observation in observations)
        {
            foreach (string existingPath in ExtractObservedExistingPaths(observation.Result))
            {
                availablePaths.Add(existingPath);
            }
        }

        return availablePaths;
    }

    private static IEnumerable<string> ExtractObservedExistingPaths(string observationResult)
    {
        foreach (Match match in Regex.Matches(
                     observationResult,
                     @"File '(?<path>[^']+)' already exists",
                     RegexOptions.IgnoreCase))
        {
            string path = NormalizeProjectPath(match.Groups["path"].Value);
            if (LooksLikeProjectPath(path))
            {
                yield return path;
            }
        }
    }

    private static List<AgentTask> PreferAttachedMentionPaths(
        IReadOnlyList<AgentTask> tasks,
        string goal,
        IReadOnlySet<string> indexedPaths)
    {
        Dictionary<string, string> attachedPathsByFileName = ExtractAttachedMentionPaths(goal)
            .Select(path => new AttachedMentionPath(path, Path.GetFileName(path)))
            .Where(path => !string.IsNullOrWhiteSpace(path.FileName))
            .GroupBy(path => path.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(path => path.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Path, StringComparer.OrdinalIgnoreCase);

        if (attachedPathsByFileName.Count == 0)
        {
            return tasks.OrderBy(task => task.Step).ToList();
        }

        return tasks
            .OrderBy(task => task.Step)
            .Select(task => task with { Argument = PreferAttachedMentionPath(task.Argument, attachedPathsByFileName, indexedPaths) })
            .ToList();
    }

    private static IEnumerable<string> ExtractAttachedMentionPaths(string goal)
    {
        foreach (Match match in Regex.Matches(goal, @"--- begin file: (?<path>.+?) ---"))
        {
            string path = NormalizeProjectPath(match.Groups["path"].Value);
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }
    }

    private static string PreferAttachedMentionPath(
        string argument,
        IReadOnlyDictionary<string, string> attachedPathsByFileName,
        IReadOnlySet<string> indexedPaths)
    {
        if (TryExtractTargetFile(argument, out string? targetFilePath) &&
            targetFilePath is not null &&
            TryGetAttachedReplacement(targetFilePath, attachedPathsByFileName, indexedPaths, out string? replacement))
        {
            return ReplaceExtractedTargetFile(argument, targetFilePath, replacement);
        }

        return TryGetAttachedReplacement(argument, attachedPathsByFileName, indexedPaths, out replacement)
            ? replacement
            : argument;
    }

    private static List<AgentTask> ResolveUniqueIndexedPathReferences(
        IReadOnlyList<AgentTask> tasks,
        IReadOnlySet<string> indexedPaths) =>
        tasks
            .OrderBy(task => task.Step)
            .Select(task => task with { Argument = ResolveUniqueIndexedPathReference(task.Action, task.Argument, indexedPaths) })
            .ToList();

    private static List<AgentTask> RewriteCreateFileForExistingDocumentation(
        IReadOnlyList<AgentTask> tasks,
        IReadOnlySet<string> availablePaths)
    {
        var orderedTasks = tasks.OrderBy(task => task.Step).ToArray();
        var result = new List<AgentTask>();
        for (int index = 0; index < orderedTasks.Length; index++)
        {
            AgentTask task = orderedTasks[index];
            if (!IsCreateFileForExistingDocumentation(task, availablePaths, out string documentationPath))
            {
                result.Add(task);
                continue;
            }

            if (HasLaterDocumentationWrite(orderedTasks, index, documentationPath))
            {
                continue;
            }

            result.Add(task with
            {
                Action = "write-documentation",
                Argument = documentationPath,
                Reason = $"Write documentation to existing file {documentationPath} instead of creating it."
            });
        }

        return RenumberTasks(result);
    }

    private static bool IsCreateFileForExistingDocumentation(
        AgentTask task,
        IReadOnlySet<string> availablePaths,
        out string documentationPath)
    {
        documentationPath = string.Empty;
        if (StringHelper.NormalizeAction(task.Action) != "create-file")
        {
            return false;
        }

        string createdPath = NormalizeProjectPath(task.Argument);
        if (!availablePaths.Contains(createdPath) || !IsDocumentationPath(createdPath))
        {
            return false;
        }

        documentationPath = createdPath;
        return true;
    }

    private static bool HasLaterDocumentationWrite(
        IReadOnlyList<AgentTask> orderedTasks,
        int currentIndex,
        string documentationPath)
    {
        for (int index = currentIndex + 1; index < orderedTasks.Count; index++)
        {
            AgentTask laterTask = orderedTasks[index];
            if (StringHelper.NormalizeAction(laterTask.Action) != "write-documentation")
            {
                continue;
            }

            string laterPath = NormalizeProjectPath(ExtractDocumentationTargetPath(laterTask.Argument));
            if (string.Equals(laterPath, documentationPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<AgentTask> RemoveRedundantEditsForCreatedSourceFiles(IReadOnlyList<AgentTask> tasks)
    {
        var createdSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AgentTask>();
        foreach (AgentTask task in tasks.OrderBy(task => task.Step))
        {
            string action = StringHelper.NormalizeAction(task.Action);
            if (action == "create-file")
            {
                string createdPath = NormalizeProjectPath(task.Argument);
                if (IsSourceOrAssetPath(createdPath))
                {
                    createdSourcePaths.Add(createdPath);
                }

                result.Add(task);
                continue;
            }

            if ((action == "apply-patch" || action == "write-code") &&
                TryExtractTargetFile(task.Argument, out string? targetFilePath) &&
                targetFilePath is not null &&
                createdSourcePaths.Contains(NormalizeProjectPath(targetFilePath)))
            {
                continue;
            }

            result.Add(task);
        }

        return RenumberTasks(result);
    }

    private static bool IsSourceOrAssetPath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".css", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDocumentationPath(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFileName(path), "README", StringComparison.OrdinalIgnoreCase);

    private static string ResolveUniqueIndexedPathReference(
        string action,
        string argument,
        IReadOnlySet<string> indexedPaths)
    {
        string normalizedAction = StringHelper.NormalizeAction(action);
        if (normalizedAction == "read" &&
            TryResolveUniqueIndexedBasename(argument, indexedPaths, out string resolvedReadPath))
        {
            return resolvedReadPath;
        }

        if ((normalizedAction == "apply-patch" ||
             normalizedAction == "write-code" ||
             normalizedAction == "write-documentation") &&
            TryExtractTargetFile(argument, out string? targetFilePath) &&
            targetFilePath is not null &&
            TryResolveUniqueIndexedBasename(targetFilePath, indexedPaths, out string resolvedTargetPath))
        {
            return ReplaceExtractedTargetFile(argument, targetFilePath, resolvedTargetPath);
        }

        if (normalizedAction == "write-documentation" &&
            TryResolveUniqueIndexedBasename(argument, indexedPaths, out string resolvedDocumentationPath))
        {
            return resolvedDocumentationPath;
        }

        return argument;
    }

    private static bool TryResolveUniqueIndexedBasename(
        string candidatePath,
        IReadOnlySet<string> indexedPaths,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        string normalizedCandidate = NormalizeProjectPath(candidatePath);
        if (indexedPaths.Contains(normalizedCandidate) ||
            !Path.HasExtension(normalizedCandidate))
        {
            return false;
        }

        if (normalizedCandidate.Contains('/', StringComparison.Ordinal) &&
            !IsSkippedProjectMapPath(normalizedCandidate))
        {
            return false;
        }

        string candidateFileName = Path.GetFileName(normalizedCandidate);
        string[] matches = indexedPaths
            .Where(path => string.Equals(Path.GetFileName(path), candidateFileName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        resolvedPath = matches[0];
        return true;
    }

    private static bool TryGetAttachedReplacement(
        string candidatePath,
        IReadOnlyDictionary<string, string> attachedPathsByFileName,
        IReadOnlySet<string> indexedPaths,
        out string replacement)
    {
        replacement = string.Empty;
        string normalizedCandidate = NormalizeProjectPath(candidatePath);
        string fileName = Path.GetFileName(normalizedCandidate);
        if (string.IsNullOrWhiteSpace(fileName) ||
            !attachedPathsByFileName.TryGetValue(fileName, out string? attachedPath) ||
            string.Equals(normalizedCandidate, attachedPath, StringComparison.OrdinalIgnoreCase) ||
            indexedPaths.Contains(normalizedCandidate))
        {
            return false;
        }

        replacement = attachedPath;
        return true;
    }

    private static string ReplaceExtractedTargetFile(string argument, string oldTargetFilePath, string replacement)
    {
        const string targetPrefix = "Target file:";
        string normalized = argument.Replace("\r\n", "\n", StringComparison.Ordinal);
        int targetIndex = normalized.IndexOf(targetPrefix, StringComparison.OrdinalIgnoreCase);
        if (targetIndex < 0)
        {
            return argument;
        }

        int pathStart = targetIndex + targetPrefix.Length;
        int pathEnd = normalized.IndexOf('\n', pathStart);
        pathEnd = pathEnd < 0 ? normalized.Length : pathEnd;
        string existingTarget = normalized[pathStart..pathEnd].Trim();
        if (!string.Equals(existingTarget, oldTargetFilePath, StringComparison.Ordinal))
        {
            return argument;
        }

        return normalized[..pathStart] + " " + replacement + normalized[pathEnd..];
    }

    private static string NormalizeProjectPath(string path)
    {
        string normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized))
        {
            normalized = Path.GetRelativePath(PathResolver.WorkspaceRoot, normalized).Replace('\\', '/');
        }

        return normalized.TrimStart('/');
    }

    private static List<AgentTask> RenumberTasks(IEnumerable<AgentTask> tasks) =>
        tasks.Select((task, index) => task with { Step = index + 1 }).ToList();

    private sealed record AttachedMentionPath(string Path, string FileName);

    private sealed record ProjectMapCacheLocation(
        string TargetDirectory,
        string CacheRootDirectory,
        string CachePath,
        string? CachePrunePathPrefix);

    private sealed class PlanCompletenessReview
    {
        public bool IsComplete { get; init; }

        public string Feedback { get; init; } = string.Empty;
    }
    
    private static void ValidateTasks(
        string goal,
        IEnumerable<AgentTask> tasks,
        IReadOnlyCollection<string> supportedActions,
        IReadOnlySet<string> indexedPaths)
    {
        AgentTask[] orderedTasks = tasks.OrderBy(task => task.Step).ToArray();
        var supportedActionSet = supportedActions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availablePaths = new HashSet<string>(indexedPaths, StringComparer.OrdinalIgnoreCase);
        var lastReadPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int expectedStep = 1;
        foreach (AgentTask task in orderedTasks)
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

            ValidateTaskPathContract(task, action, availablePaths, lastReadPaths);
            expectedStep++;
        }

        ValidatePlanCompletesUserRequest(orderedTasks);
        ValidateWriteReportPlacement(orderedTasks);
        ValidateMentionedDocumentationTargets(goal, orderedTasks);
    }

    private static void ValidatePlanCompletesUserRequest(IReadOnlyList<AgentTask> orderedTasks)
    {
        if (orderedTasks.Count == 0)
        {
            return;
        }

        AgentTask lastTask = orderedTasks[^1];
        string lastAction = StringHelper.NormalizeAction(lastTask.Action);
        if (!IsContextGatheringAction(lastAction))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Planner step {lastTask.Step} ends with context-gathering action '{lastTask.Action}'. " +
            "Add an implementation, review, documentation, shell, or write-report step that completes the user request.");
    }

    private static void ValidateWriteReportPlacement(IReadOnlyList<AgentTask> orderedTasks)
    {
        for (int index = 0; index < orderedTasks.Count - 1; index++)
        {
            AgentTask task = orderedTasks[index];
            if (StringHelper.NormalizeAction(task.Action) == "write-report")
            {
                throw new InvalidOperationException(
                    $"Planner step {task.Step} uses write-report before the plan is complete. " +
                    "Use write-report only as the final step, after all requested reads, creates, edits, and documentation writes.");
            }
        }
    }

    private static void ValidateMentionedDocumentationTargets(string goal, IReadOnlyList<AgentTask> orderedTasks)
    {
        if (!HasDocumentationWriteIntent(goal))
        {
            return;
        }

        string[] requestedDocumentationFiles = ExtractMentionedDocumentationFileNames(goal).ToArray();
        if (requestedDocumentationFiles.Length == 0)
        {
            return;
        }

        var plannedDocumentationFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AgentTask task in orderedTasks)
        {
            string action = StringHelper.NormalizeAction(task.Action);
            string? targetPath = action switch
            {
                "create-file" => task.Argument,
                "write-documentation" => ExtractDocumentationTargetPath(task.Argument),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(targetPath))
            {
                plannedDocumentationFiles.Add(Path.GetFileName(NormalizeProjectPath(targetPath)));
            }
        }

        foreach (string requestedFile in requestedDocumentationFiles)
        {
            if (!plannedDocumentationFiles.Contains(requestedFile))
            {
                throw new InvalidOperationException(
                    $"User request names documentation file '{requestedFile}', but the plan has no create-file or write-documentation step for it.");
            }
        }
    }

    private static bool HasDocumentationWriteIntent(string goal) =>
        Regex.IsMatch(
            goal,
            @"\b(add|create|describe|document|edit|expand|generate|improve|rewrite|update|write)\b",
            RegexOptions.IgnoreCase);

    private static IEnumerable<string> ExtractMentionedDocumentationFileNames(string goal)
    {
        foreach (Match match in Regex.Matches(
                     goal,
                     @"(?<![\w./-])(?<path>(?:[\w.-]+/)*[\w.-]+\.(?:md|mdx))(?![\w./-])",
                     RegexOptions.IgnoreCase))
        {
            string fileName = Path.GetFileName(NormalizeProjectPath(match.Groups["path"].Value));
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                yield return fileName;
            }
        }
    }

    private static void ValidateTaskPathContract(
        AgentTask task,
        string action,
        ISet<string> availablePaths,
        ISet<string> lastReadPaths)
    {
        switch (action)
        {
            case "read":
            {
                string readPath = NormalizeProjectPath(task.Argument);
                if (!availablePaths.Contains(readPath))
                {
                    throw new InvalidOperationException(
                        $"Planner step {task.Step} reads '{task.Argument}', but that path is neither present in Workspace context nor created earlier in the plan.");
                }

                lastReadPaths.Add(readPath);
                return;
            }

            case "create-file":
            {
                string createdPath = NormalizeProjectPath(task.Argument);
                if (!LooksLikeProjectPath(createdPath))
                {
                    throw new InvalidOperationException(
                        $"Planner step {task.Step} create-file argument must be a concrete file path.");
                }

                availablePaths.Add(createdPath);
                return;
            }

            case "apply-patch":
            case "write-code":
            {
                ValidateImplementationTarget(task, action, availablePaths, lastReadPaths);
                return;
            }

            case "write-documentation":
            {
                string documentationPath = NormalizeProjectPath(ExtractDocumentationTargetPath(task.Argument));
                if (!LooksLikeProjectPath(documentationPath))
                {
                    throw new InvalidOperationException(
                        $"Planner step {task.Step} write-documentation argument must name a concrete documentation file path.");
                }

                if (!IsDocumentationPath(documentationPath))
                {
                    throw new InvalidOperationException(
                        $"Planner step {task.Step} uses write-documentation for non-Markdown target '{documentationPath}'. Use apply-patch or write-code for source files.");
                }

                if (!availablePaths.Contains(documentationPath))
                {
                    throw new InvalidOperationException(
                        $"Planner step {task.Step} writes documentation to '{documentationPath}', but that file is neither indexed nor created earlier in the plan. " +
                        $"Insert an earlier create-file step with Argument '{documentationPath}' before this write-documentation step.");
                }
                return;
            }
        }
    }

    private static void ValidateImplementationTarget(
        AgentTask task,
        string action,
        ISet<string> availablePaths,
        ISet<string> lastReadPaths)
    {
        if (!TryExtractTargetFile(task.Argument, out string? targetFilePath) ||
            string.IsNullOrWhiteSpace(targetFilePath))
        {
            throw new InvalidOperationException(
                $"Planner step {task.Step} {action} argument must include 'Target file: <path>' followed by concrete instructions.");
        }

        string normalizedTargetPath = NormalizeProjectPath(targetFilePath);
        if (!availablePaths.Contains(normalizedTargetPath))
        {
            throw new InvalidOperationException(
                $"Planner step {task.Step} targets '{normalizedTargetPath}', but that file is neither indexed nor created earlier in the plan.");
        }

        if (!lastReadPaths.Contains(normalizedTargetPath))
        {
            throw new InvalidOperationException(
                $"Planner step {task.Step} targets '{normalizedTargetPath}' before a successful read step for that same file.");
        }
    }

    private static string ExtractDocumentationTargetPath(string argument)
    {
        const string targetPrefix = "Target file:";
        string normalized = argument.Replace("\r\n", "\n", StringComparison.Ordinal);
        int targetIndex = normalized.IndexOf(targetPrefix, StringComparison.OrdinalIgnoreCase);
        if (targetIndex < 0)
        {
            return argument.Trim();
        }

        int pathStart = targetIndex + targetPrefix.Length;
        int pathEnd = normalized.IndexOf('\n', pathStart);
        return (pathEnd < 0 ? normalized[pathStart..] : normalized[pathStart..pathEnd]).Trim();
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
