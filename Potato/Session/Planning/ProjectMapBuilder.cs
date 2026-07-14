using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Potato.Session.Tasks;

namespace Potato.Session;

public sealed class ProjectMapBuilder
{
    private const int ProjectMapCacheSchemaVersion = 1;
    private const string ProjectMapCacheDirectoryName = ".potato";
    private const string ProjectMapCacheFileName = "project-map-cache.json";
    private const int DefaultSearchResultLimit = 12;

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

    private static readonly JsonSerializerOptions ProjectMapCacheJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string> BuildProjectMapAsync(
        string targetDirectory,
        IChatClient chatClient,
        CancellationToken cancellationToken)
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
                ProjectMapCacheEntry? cachedEntry = GetValidProjectMapCacheEntry(cache, cacheRelativePath, file, promptHash);
                if (cachedEntry is not null)
                {
                    progress.Update(BuildProjectMapProgressMessage(index + 1, files.Length, relativePath, cached: true));
                    if (UpdateProjectMapCacheEntry(cache, cacheRelativePath, file, cachedEntry.FileHash, promptHash, cachedEntry.Summary))
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
                    FileHash = string.Empty,
                    PromptHash = promptHash,
                    Summary = summary
                };
                SaveProjectMapCache(cacheLocation.CachePath, cache);
                AppendProjectMapSummary(builder, relativePath, summary);
            }
        }

        return builder.ToString();
    }

    public Task<string> BuildProjectMapHeaderAsync(string targetDirectory, CancellationToken cancellationToken)
    {
        ProjectMapCacheLocation cacheLocation = GetProjectMapCacheLocation(targetDirectory);
        ProjectMapCache cache = LoadProjectMapCache(cacheLocation.CachePath);
        var builder = new StringBuilder();
        builder.AppendLine($"ProjectMap root: {cacheLocation.TargetDirectory}");
        builder.AppendLine($"ProjectMap cache: {cache.Entries.Count} cached entries.");
        builder.AppendLine("ProjectMap entries are validated lazily during build and search.");
        return Task.FromResult(builder.ToString());
    }

    public async Task<string> SearchProjectMapAsync(
        string targetDirectory,
        string query,
        int maxResults,
        IChatClient? chatClient,
        CancellationToken cancellationToken)
    {
        ProjectMapCacheLocation cacheLocation = GetProjectMapCacheLocation(targetDirectory);
        ValidateProjectMapCacheMetadata(cacheLocation);
        maxResults = Math.Clamp(maxResults <= 0 ? DefaultSearchResultLimit : maxResults, 1, 30);
        query = query.Trim();

        FileInfo[] files = EnumerateProjectMapFiles(cacheLocation.TargetDirectory)
            .Where(IsProjectMapFile)
            .OrderBy(file => ToRelativeProjectMapPath(cacheLocation.TargetDirectory, file.FullName), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] tokens = TokenizeSearchQuery(query);
        ProjectMapSearchCandidate[] candidates = files
            .Select(file => ScoreProjectMapSearchCandidate(cacheLocation.TargetDirectory, file, tokens))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine($"ProjectMap root: {cacheLocation.TargetDirectory}");
        builder.AppendLine($"ProjectMap search query: {(string.IsNullOrWhiteSpace(query) ? "(default important files)" : query)}");
        builder.AppendLine($"ProjectMap search results: {candidates.Length}/{files.Length}");

        if (candidates.Length == 0)
        {
            builder.AppendLine("No indexed files matched the query.");
            return builder.ToString();
        }

        string promptHash = ComputeHash(Prompts.PromptLibrary.BuildProjectMapCacheKey);
        ProjectMapCache cache = LoadProjectMapCache(cacheLocation.CachePath);

        using PotatoConsole.IProgressReporter progress =
            PotatoConsole.StartProgress($"Searching ProjectMap for {candidates.Length} matched file(s)...");
        for (int index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectMapSearchCandidate candidate = candidates[index];
            FileInfo file = candidate.File;
            string cacheRelativePath = ToRelativeProjectMapPath(cacheLocation.CacheRootDirectory, file.FullName);
            ProjectMapCacheEntry? cachedEntry = GetValidProjectMapCacheEntry(cache, cacheRelativePath, file, promptHash);
            string summary;
            if (cachedEntry is not null)
            {
                summary = cachedEntry.Summary;
                progress.Update(BuildProjectMapProgressMessage(index + 1, candidates.Length, candidate.RelativePath, cached: true));
            }
            else
            {
                progress.Update(BuildProjectMapProgressMessage(index + 1, candidates.Length, candidate.RelativePath, cached: false));
                if (chatClient is null)
                {
                    summary = "- Summary not cached yet. Read this file directly if it looks relevant.";
                }
                else
                {
                    string content = await File.ReadAllTextAsync(file.FullName, cancellationToken);
                    summary = await SummarizeProjectFileAsync(candidate.RelativePath, content, chatClient, cancellationToken);
                    cache.Entries[cacheRelativePath] = new ProjectMapCacheEntry
                    {
                        LastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks,
                        Length = file.Length,
                        FileHash = string.Empty,
                        PromptHash = promptHash,
                        Summary = summary
                    };
                    SaveProjectMapCache(cacheLocation.CachePath, cache);
                }
            }

            AppendProjectMapSummary(builder, candidate.RelativePath, summary);
        }

        return builder.ToString();
    }

    private static string BuildProjectMapProgressMessage(int currentFileIndex, int totalFiles, string relativePath, bool cached)
    {
        int percentage = totalFiles == 0
            ? 100
            : (int)Math.Round(currentFileIndex * 100.0 / totalFiles);

        int fileNumberWidth = Math.Max(1, totalFiles.ToString().Length);
        string current = currentFileIndex.ToString().PadLeft(fileNumberWidth);
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
        if (PlanningPathUtilities.IsSkippedProjectMapPath(file.FullName))
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

    private static ProjectMapSearchCandidate ScoreProjectMapSearchCandidate(
        string projectMapRoot,
        FileInfo file,
        IReadOnlyList<string> tokens)
    {
        string relativePath = ToRelativeProjectMapPath(projectMapRoot, file.FullName);
        if (tokens.Count == 0)
        {
            int defaultScore = ProjectMapFileNames.Any(name => file.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ? 100
                : file.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                  file.Extension.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
                    ? 90
                    : 1;
            return new ProjectMapSearchCandidate(file, relativePath, defaultScore);
        }

        string searchablePath = relativePath.ToLowerInvariant();
        int score = 0;
        foreach (string token in tokens)
        {
            if (searchablePath.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }
            else if (Path.GetFileName(searchablePath).Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 80;
            }
            else if (searchablePath.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }
        }

        if (score == 0 && TryReadSearchPreview(file.FullName, out string preview))
        {
            foreach (string token in tokens)
            {
                if (preview.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    score += 10;
                }
            }
        }

        return new ProjectMapSearchCandidate(file, relativePath, score);
    }

    private static string[] TokenizeSearchQuery(string query) =>
        query.Split([' ', '\t', '\r', '\n', ',', ';', ':', '|', '"', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool TryReadSearchPreview(string filePath, out string preview)
    {
        const int maxCharacters = 20000;
        preview = string.Empty;
        try
        {
            using var reader = new StreamReader(filePath);
            char[] buffer = new char[maxCharacters];
            int read = reader.ReadBlock(buffer, 0, buffer.Length);
            preview = new string(buffer, 0, read);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ProjectMapCacheValidationResult ValidateProjectMapCacheMetadata(ProjectMapCacheLocation cacheLocation)
    {
        FileInfo[] files = EnumerateProjectMapFiles(cacheLocation.TargetDirectory)
            .Where(IsProjectMapFile)
            .ToArray();
        var currentFilesByCachePath = files.ToDictionary(
            file => ToRelativeProjectMapPath(cacheLocation.CacheRootDirectory, file.FullName),
            StringComparer.OrdinalIgnoreCase);

        ProjectMapCache cache = LoadProjectMapCache(cacheLocation.CachePath);
        string promptHash = ComputeHash(Prompts.PromptLibrary.BuildProjectMapCacheKey);
        int originalEntryCount = cache.Entries.Count;

        var currentRelativePaths = currentFilesByCachePath.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed = PruneDeletedProjectMapEntries(cache, currentRelativePaths, cacheLocation.CachePrunePathPrefix);

        foreach ((string cacheRelativePath, ProjectMapCacheEntry entry) in cache.Entries.ToArray())
        {
            if (!IsPathInCachePruneScope(cacheRelativePath, cacheLocation.CachePrunePathPrefix))
            {
                continue;
            }

            if (!currentFilesByCachePath.TryGetValue(cacheRelativePath, out FileInfo? file) ||
                entry.LastWriteTimeUtcTicks != file.LastWriteTimeUtc.Ticks ||
                entry.Length != file.Length ||
                !string.Equals(entry.PromptHash, promptHash, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(entry.Summary))
            {
                cache.Entries.Remove(cacheRelativePath);
                changed = true;
            }
        }

        if (changed)
        {
            SaveProjectMapCache(cacheLocation.CachePath, cache);
        }

        return new ProjectMapCacheValidationResult(
            cache.Entries.Count(path => IsPathInCachePruneScope(path.Key, cacheLocation.CachePrunePathPrefix)),
            originalEntryCount - cache.Entries.Count);
    }

    private static ProjectMapCacheEntry? GetValidProjectMapCacheEntry(
        ProjectMapCache cache,
        string relativePath,
        FileInfo file,
        string promptHash)
    {
        if (!cache.Entries.TryGetValue(relativePath, out ProjectMapCacheEntry? entry))
        {
            return null;
        }

        return entry.LastWriteTimeUtcTicks == file.LastWriteTimeUtc.Ticks &&
               entry.Length == file.Length &&
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

    private static async Task<string> SummarizeProjectFileAsync(
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

    private sealed record ProjectMapCacheLocation(
        string TargetDirectory,
        string CacheRootDirectory,
        string CachePath,
        string? CachePrunePathPrefix);

    private sealed record ProjectMapSearchCandidate(FileInfo File, string RelativePath, int Score);

    private sealed record ProjectMapCacheValidationResult(int ValidEntries, int RemovedEntries);

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
