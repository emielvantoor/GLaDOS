using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Potato.Tools;

public class AgentTools(ExecutionMemory memory, CurrentChatClientState chatClientState, PotatoRuntimeOptions options, FimClient fimClient)
{
    private const int DefaultCommandTimeoutSeconds = 60;
    private const int MaxCommandTimeoutSeconds = 600;
    private const int MaxPatchCharacters = 200_000;
    private const int MaxPurposeInferenceCharacters = 12_000;
    private const int MaxFileRangeLines = 200;
    private const int MaxFimEditLines = 60;
    private const int MaxFimContextLines = 60;
    private const int MaxFimContextCharacters = 12_000;
    private const long MaxSearchFileBytes = 1_000_000;
    private const int MaxSearchFiles = 5000;

    private static readonly HashSet<string> SkippedSearchExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".avi", ".bin", ".bmp", ".dll", ".doc", ".docx", ".dylib", ".exe", ".gif", ".ico", ".jar",
        ".jpeg", ".jpg", ".lock", ".mov", ".mp3", ".mp4", ".obj", ".pdf", ".png", ".pdb", ".so", ".webp",
        ".zip"
    };

    public int ToolInvocationCount { get; private set; }

    public int SuccessfulEditCount { get; private set; }

    public CancellationToken CurrentCancellationToken { get; set; }

    public Func<string, bool>? ToolInvocationAllowed { get; set; }

    public Func<string, string>? ToolInvocationRejectionReason { get; set; }

    private bool toolResultWritten;
    private readonly object toolInvocationLock = new();
    private int? maxToolInvocationsThisIteration;
    private int toolInvocationsThisIteration;

    public void BeginToolInvocationBatch(int maxToolInvocations)
    {
        lock (toolInvocationLock)
        {
            maxToolInvocationsThisIteration = Math.Max(1, maxToolInvocations);
            toolInvocationsThisIteration = 0;
        }
    }

    public void EndToolInvocationBatch()
    {
        lock (toolInvocationLock)
        {
            maxToolInvocationsThisIteration = null;
            toolInvocationsThisIteration = 0;
        }
    }

    internal bool TryReserveExternalToolInvocation(string toolName, out string rejectionReason) =>
        TryReserveToolInvocation(toolName, out rejectionReason);

    internal string RejectExternalToolInvocation(string toolName, string reason) =>
        RejectToolInvocation(toolName, reason);

    public Task<bool> IsFimAvailableAsync(CancellationToken cancellationToken) =>
        fimClient.IsAvailableAsync(cancellationToken);

    private bool TryReserveToolInvocation(string toolName, out string rejectionReason)
    {
        CurrentCancellationToken.ThrowIfCancellationRequested();
        lock (toolInvocationLock)
        {
            ToolInvocationCount++;
            if (maxToolInvocationsThisIteration is not { } maxToolInvocations ||
                toolInvocationsThisIteration < maxToolInvocations)
            {
                toolInvocationsThisIteration++;
                if (ToolInvocationAllowed is null || ToolInvocationAllowed(toolName))
                {
                    rejectionReason = string.Empty;
                    return true;
                }

                rejectionReason = ToolInvocationRejectionReason?.Invoke(toolName) ??
                                  $"Rejected {toolName}: this tool does not match the current planned step/substep.";
                return false;
            }
        }

        rejectionReason =
            $"Rejected {toolName}: this execution step already used its permitted tool call. " +
            "Wait for the latest observation before choosing another tool.";
        return false;
    }

    private string RejectToolInvocation(string toolName, string reason)
    {
        WriteCompactToolResult(false, "Tool rejected", toolName);
        return StoreAndReturn(toolName, reason);
    }

    [Description("Gets the current local system date and time.")]
    public string GetCurrentTime()
    {
        if (!TryReserveToolInvocation(nameof(GetCurrentTime), out string rejectionReason))
        {
            return RejectToolInvocation(nameof(GetCurrentTime), rejectionReason);
        }

        WriteToolCall(nameof(GetCurrentTime), []);
        string result = $"The current local time is: {DateTime.Now:F}";
        memory.Add(nameof(GetCurrentTime), result);
        return result;
    }

    [Description("Reads the contents of a specific text file from disk.")]
    public string ReadFileContent([Description("The path to the file. Absolute paths are accepted; relative paths resolve from the current working directory.")] string filePath)
    {
        if (!TryReserveToolInvocation(nameof(ReadFileContent), out string rejectionReason))
        {
            return RejectToolInvocation(nameof(ReadFileContent), rejectionReason);
        }

        string? resolvedPath = ResolveReadableFilePath(filePath);
        WriteToolCall(nameof(ReadFileContent),
        [
            ("filePath", filePath),
            ("resolvedPath", resolvedPath ?? "(unresolved)")
        ]);

        if (IsPlaceholderPath(filePath) && resolvedPath is null)
        {
            return StoreAndReturn(nameof(ReadFileContent), "Error: The file path is a placeholder. Use a real path from the directory listing or attached file header.");
        }

        if (resolvedPath is null || !File.Exists(resolvedPath))
        {
            return StoreAndReturn(nameof(ReadFileContent), $"Error: File '{filePath}' does not exist. Current working directory: {Environment.CurrentDirectory}");
        }

        return StoreAndReturn($"{nameof(ReadFileContent)} {resolvedPath}", File.ReadAllText(resolvedPath));
    }

    [Description("Reads an inclusive line range from a specific text file without returning the whole file.")]
    public string ReadFileRange(
        [Description("The path to the file. Absolute paths are accepted; relative paths resolve from the current working directory.")] string filePath,
        [Description("The first 1-based line number to include in the result.")] int startLine,
        [Description("The last 1-based line number to include in the result.")] int endLine)
    {
        if (!TryReserveToolInvocation(nameof(ReadFileRange), out string rejectionReason))
        {
            return RejectToolInvocation(nameof(ReadFileRange), rejectionReason);
        }

        string? resolvedPath = ResolveReadableFilePath(filePath);
        WriteToolCall(nameof(ReadFileRange),
        [
            ("filePath", filePath),
            ("resolvedPath", resolvedPath ?? "(unresolved)"),
            ("startLine", startLine.ToString()),
            ("endLine", endLine.ToString())
        ]);

        if (IsPlaceholderPath(filePath) && resolvedPath is null)
        {
            return StoreAndReturn(nameof(ReadFileRange), "Error: The file path is a placeholder. Use a real path from the directory listing or attached file header.");
        }

        if (resolvedPath is null || !File.Exists(resolvedPath))
        {
            return StoreAndReturn(nameof(ReadFileRange), $"Error: File '{filePath}' does not exist. Current working directory: {Environment.CurrentDirectory}");
        }

        if (startLine < 1 || endLine < 1)
        {
            return StoreAndReturn(nameof(ReadFileRange), "Error: Line numbers must be 1 or greater.");
        }

        if (endLine < startLine)
        {
            return StoreAndReturn(nameof(ReadFileRange), "Error: endLine must be greater than or equal to startLine.");
        }

        int requestedLines = endLine - startLine + 1;
        if (requestedLines > MaxFileRangeLines)
        {
            return StoreAndReturn(
                nameof(ReadFileRange),
                $"Error: Requested range is too large ({requestedLines} lines). Maximum supported range is {MaxFileRangeLines} lines.");
        }

        try
        {
            var builder = new StringBuilder();
            int totalLines = 0;
            int returnedLines = 0;

            using var reader = new StreamReader(File.OpenRead(resolvedPath));
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                totalLines++;
                if (totalLines < startLine || totalLines > endLine)
                {
                    continue;
                }

                if (returnedLines == 0)
                {
                    builder.AppendLine($"File: {resolvedPath}");
                    builder.AppendLine($"Requested range: {startLine}-{endLine}");
                }

                builder.AppendLine($"{totalLines}: {line}");
                returnedLines++;
            }

            if (returnedLines == 0)
            {
                return StoreAndReturn(
                    nameof(ReadFileRange),
                    totalLines == 0
                        ? $"Error: File '{resolvedPath}' is empty."
                        : $"Error: Requested start line {startLine} is beyond the end of the file ({totalLines} total line(s)).");
            }

            builder.AppendLine($"Total lines in file: {totalLines}");
            if (endLine > totalLines)
            {
                builder.AppendLine($"Note: returned through EOF at line {totalLines}.");
            }

            return StoreAndReturn($"{nameof(ReadFileRange)} {resolvedPath} [{startLine}-{endLine}]", builder.ToString().TrimEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return StoreAndReturn(nameof(ReadFileRange), $"Error: Could not read file range: {ex.Message}");
        }
    }

    [Description("Lists non-hidden project files and directories without using the shell. Hidden folders and common build/dependency folders are skipped. Use this for read-only project discovery before choosing files to inspect.")]
    public string ListFiles(
        [Description("Optional directory path. Absolute paths are accepted; relative paths resolve from the current working directory. Leave empty to list the current working directory.")] string? directoryPath = null,
        [Description("Whether to recurse into subdirectories. Defaults to false.")] bool recursive = false,
        [Description("Maximum number of entries to return. Defaults to 200 and is capped at 1000.")] int maxEntries = 200)
    {
        if (!TryReserveToolInvocation(nameof(ListFiles), out string rejectionReason))
        {
            return RejectToolInvocation(nameof(ListFiles), rejectionReason);
        }

        string? resolvedPath = ResolveReadableDirectoryPath(directoryPath);
        WriteToolCall(nameof(ListFiles),
        [
            ("directoryPath", directoryPath ?? Environment.CurrentDirectory),
            ("resolvedPath", resolvedPath ?? "(unresolved)"),
            ("recursive", recursive.ToString()),
            ("maxEntries", maxEntries.ToString())
        ]);

        if (resolvedPath is null || !Directory.Exists(resolvedPath))
        {
            return StoreAndReturn(nameof(ListFiles), $"Error: Directory '{directoryPath}' does not exist. Current working directory: {Environment.CurrentDirectory}");
        }

        maxEntries = Math.Clamp(maxEntries, 1, 1000);
        var builder = new StringBuilder();
        builder.AppendLine($"Directory: {resolvedPath}");
        builder.AppendLine($"Mode: {(recursive ? "recursive" : "top-level")}");

        IEnumerable<FileSystemInfo> entries = EnumerateFileSystemEntries(resolvedPath, recursive);
        int count = 0;
        foreach (FileSystemInfo entry in entries.Take(maxEntries + 1))
        {
            if (count == maxEntries)
            {
                builder.AppendLine($"... truncated after {maxEntries} entries");
                break;
            }

            string relativePath = Path.GetRelativePath(resolvedPath, entry.FullName);
            if (entry is DirectoryInfo)
            {
                builder.AppendLine($"[dir]  {relativePath}/");
            }
            else if (entry is FileInfo file)
            {
                builder.AppendLine($"[file] {relativePath} ({file.Length} bytes)");
            }

            count++;
        }

        if (count == 0)
        {
            builder.AppendLine("(empty)");
        }

        string mode = recursive ? "recursive" : "top-level";
        return StoreAndReturn($"{nameof(ListFiles)} {resolvedPath} ({mode}, maxEntries={maxEntries})", builder.ToString());
    }

    [Description("Lists project and solution manifest files in a repository without dumping the whole directory tree. Hidden folders and common build/dependency folders are skipped. Use this before documenting all projects in a repository.")]
    public string ListProjectFiles(
        [Description("Optional directory path. Absolute paths are accepted; relative paths resolve from the current working directory. Leave empty to inspect the current working directory.")] string? directoryPath = null)
    {
        if (!TryReserveToolInvocation(nameof(ListProjectFiles), out string rejectionReason))
        {
            return RejectToolInvocation(nameof(ListProjectFiles), rejectionReason);
        }

        string? resolvedPath = ResolveReadableDirectoryPath(directoryPath);
        WriteToolCall(nameof(ListProjectFiles),
        [
            ("directoryPath", directoryPath ?? Environment.CurrentDirectory),
            ("resolvedPath", resolvedPath ?? "(unresolved)")
        ]);

        if (resolvedPath is null || !Directory.Exists(resolvedPath))
        {
            return StoreAndReturn(nameof(ListProjectFiles), $"Error: Directory '{directoryPath}' does not exist. Current working directory: {Environment.CurrentDirectory}");
        }

        FileInfo[] projectFiles = EnumerateSearchFiles(resolvedPath, recursive: true)
            .Where(IsProjectManifestFile)
            .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine($"Directory: {resolvedPath}");
        builder.AppendLine("Project manifests:");

        if (projectFiles.Length == 0)
        {
            builder.AppendLine("(none found)");
        }
        else
        {
            foreach (FileInfo file in projectFiles)
            {
                string relativePath = Path.GetRelativePath(resolvedPath, file.FullName);
                builder.AppendLine($"[{ProjectManifestKind(file)}] {relativePath}");
            }
        }

        string[] projectDirectories = projectFiles
            .Select(file => Path.GetRelativePath(resolvedPath, file.DirectoryName ?? resolvedPath))
            .Select(path => path == "." ? Path.GetFileName(resolvedPath) : path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        builder.AppendLine("Project directories:");
        if (projectDirectories.Length == 0)
        {
            builder.AppendLine("(none found)");
        }
        else
        {
            foreach (string projectDirectory in projectDirectories)
            {
                builder.AppendLine($"[dir] {projectDirectory}/");
            }
        }

        return StoreAndReturn($"{nameof(ListProjectFiles)} {resolvedPath}", builder.ToString());
    }

    [Description("Searches inside text/code file contents for one or more terms. Use this when you know text, symbols, or code snippets to locate; it does not search by file name. Provide multiple terms separated by '|'.")]
    public string SearchFileContents(
        [Description("Text/code content term or terms to find inside files. Multiple terms can be separated with '|', for example 'TODO|class Foo|error'.")] string searchTerms,
        [Description("Optional directory whose text/code file contents should be searched. Absolute paths are accepted; relative paths resolve from the current working directory. Leave empty to search the current working directory.")] string? directoryPath = null,
        [Description("Optional single file whose text/code contents should be searched. Absolute paths are accepted; relative paths resolve from the current working directory. When provided, this takes precedence over directoryPath.")] string? filePath = null,
        [Description("Whether to recurse into subdirectories. Defaults to true.")] bool recursive = true,
        [Description("Whether matching should be case-sensitive. Defaults to false.")] bool matchCase = false,
        [Description("Maximum number of matching lines to return. Defaults to 100 and is capped at 500.")] int maxMatches = 100)
    {
        if (!TryReserveToolInvocation(nameof(SearchFileContents), out string rejectionReason))
        {
            return RejectToolInvocation(nameof(SearchFileContents), rejectionReason);
        }

        string? resolvedFilePath = string.IsNullOrWhiteSpace(filePath) ? null : ResolveReadableFilePath(filePath);
        string? resolvedPath = resolvedFilePath is null ? ResolveReadableDirectoryPath(directoryPath) : Path.GetDirectoryName(resolvedFilePath);
        string[] terms = ParseSearchTerms(searchTerms);
        WriteToolCall(nameof(SearchFileContents),
        [
            ("searchTerms", searchTerms ?? string.Empty),
            ("directoryPath", directoryPath ?? Environment.CurrentDirectory),
            ("filePath", filePath ?? string.Empty),
            ("resolvedFilePath", resolvedFilePath ?? string.Empty),
            ("resolvedPath", resolvedPath ?? "(unresolved)"),
            ("recursive", recursive.ToString()),
            ("matchCase", matchCase.ToString()),
            ("maxMatches", maxMatches.ToString())
        ]);

        if (terms.Length == 0)
        {
            return StoreAndReturn(nameof(SearchFileContents), "Error: No search terms were provided. Use '|' to separate multiple terms.");
        }

        if (!string.IsNullOrWhiteSpace(filePath) && (resolvedFilePath is null || !File.Exists(resolvedFilePath)))
        {
            return StoreAndReturn(nameof(SearchFileContents), $"Error: File '{filePath}' does not exist. Current working directory: {Environment.CurrentDirectory}");
        }

        if (resolvedPath is null || !Directory.Exists(resolvedPath))
        {
            return StoreAndReturn(nameof(SearchFileContents), $"Error: Directory '{directoryPath}' does not exist. Current working directory: {Environment.CurrentDirectory}");
        }

        maxMatches = Math.Clamp(maxMatches, 1, 500);
        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        bool singleFileSearch = resolvedFilePath is not null;
        var builder = new StringBuilder();
        if (singleFileSearch)
        {
            builder.AppendLine($"File: {resolvedFilePath}");
        }
        else
        {
            builder.AppendLine($"Directory: {resolvedPath}");
            builder.AppendLine($"Mode: {(recursive ? "recursive" : "top-level")}");
        }

        builder.AppendLine($"Terms: {string.Join(" | ", terms)}");

        int scannedFiles = 0;
        int skippedFiles = 0;
        int matchCount = 0;
        bool truncated = false;
        IEnumerable<FileInfo> files = resolvedFilePath is not null
            ? [new FileInfo(resolvedFilePath)]
            : EnumerateSearchFiles(resolvedPath, recursive);

        foreach (FileInfo file in files)
        {
            CurrentCancellationToken.ThrowIfCancellationRequested();

            if (!ShouldSearchFile(file))
            {
                skippedFiles++;
                continue;
            }

            scannedFiles++;
            if (scannedFiles > MaxSearchFiles)
            {
                truncated = true;
                break;
            }

            if (!TryReadFileContent(file.FullName, out string content, out _))
            {
                skippedFiles++;
                continue;
            }

            if (LooksBinary(content))
            {
                skippedFiles++;
                continue;
            }

            string[] lines = NormalizeLines(content);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string? matchedTerm = terms.FirstOrDefault(term => line.Contains(term, comparison));
                if (matchedTerm is null)
                {
                    continue;
                }

                if (matchCount == 0)
                {
                    builder.AppendLine("Matches:");
                }

                string relativePath = singleFileSearch
                    ? Path.GetFileName(file.FullName)
                    : Path.GetRelativePath(resolvedPath, file.FullName);
                builder.AppendLine($"{relativePath}:{i + 1}: [{matchedTerm}] {Truncate(line.Trim(), 220)}");
                matchCount++;

                if (matchCount >= maxMatches)
                {
                    truncated = true;
                    break;
                }
            }

            if (truncated)
            {
                break;
            }
        }

        if (matchCount == 0)
        {
            builder.AppendLine("Matches: none");
        }

        builder.AppendLine($"Scanned files: {Math.Min(scannedFiles, MaxSearchFiles)}");
        builder.AppendLine($"Skipped files: {skippedFiles}");
        if (truncated)
        {
            builder.AppendLine($"... truncated after {matchCount} match(es) or {MaxSearchFiles} scanned file(s)");
        }

        string sourcePath = resolvedFilePath ?? resolvedPath;
        return StoreAndReturn($"{nameof(SearchFileContents)} {sourcePath} ({string.Join(" | ", terms)})", builder.ToString());
    }

    [Description("Searches for non-hidden files and directories by name, extension, or relative path. Hidden folders and common build/dependency folders are skipped. Use this when you know part of a file/folder name or an extension; it does not search file contents. Provide multiple terms separated by '|'.")]
    public string SearchFiles(
        [Description("File name, extension, or relative path term or terms. Multiple terms can be separated with '|', for example 'AgentTools|.cs|Prompt'.")] string searchTerms,
        [Description("Optional directory whose files should be searched. Absolute paths are accepted; relative paths resolve from the current working directory. Leave empty to search the current working directory.")] string? directoryPath = null,
        [Description("Whether to recurse into subdirectories. Defaults to true.")] bool recursive = true,
        [Description("Whether matching should be case-sensitive. Defaults to false.")] bool matchCase = false,
        [Description("Maximum number of matching files to return. Defaults to 200 and is capped at 1000.")] int maxMatches = 200)
    {
        if (!TryReserveToolInvocation(nameof(SearchFiles), out string rejectionReason))
        {
            return RejectToolInvocation(nameof(SearchFiles), rejectionReason);
        }

        string? resolvedPath = ResolveReadableDirectoryPath(directoryPath);
        string[] terms = ParseSearchTerms(searchTerms);
        WriteToolCall(nameof(SearchFiles),
        [
            ("searchTerms", searchTerms ?? string.Empty),
            ("directoryPath", directoryPath ?? Environment.CurrentDirectory),
            ("resolvedPath", resolvedPath ?? "(unresolved)"),
            ("recursive", recursive.ToString()),
            ("matchCase", matchCase.ToString()),
            ("maxMatches", maxMatches.ToString())
        ]);

        if (terms.Length == 0)
        {
            return StoreAndReturn(nameof(SearchFiles), "Error: No search terms were provided. Use '|' to separate multiple terms.");
        }

        if (resolvedPath is null || !Directory.Exists(resolvedPath))
        {
            return StoreAndReturn(nameof(SearchFiles), $"Error: Directory '{directoryPath}' does not exist. Current working directory: {Environment.CurrentDirectory}");
        }

        maxMatches = Math.Clamp(maxMatches, 1, 1000);
        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var builder = new StringBuilder();
        builder.AppendLine($"Directory: {resolvedPath}");
        builder.AppendLine($"Mode: {(recursive ? "recursive" : "top-level")}");
        builder.AppendLine($"Terms: {string.Join(" | ", terms)}");

        int scannedEntries = 0;
        int matchCount = 0;
        bool truncated = false;

        foreach (FileSystemInfo entry in EnumerateSearchEntries(resolvedPath, recursive))
        {
            CurrentCancellationToken.ThrowIfCancellationRequested();

            scannedEntries++;
            if (scannedEntries > MaxSearchFiles)
            {
                truncated = true;
                break;
            }

            string relativePath = Path.GetRelativePath(resolvedPath, entry.FullName);
            string? matchedTerm = terms.FirstOrDefault(term => FileSystemEntryMatchesTerm(entry, relativePath, term, comparison));
            if (matchedTerm is null)
            {
                continue;
            }

            if (matchCount == 0)
            {
                builder.AppendLine("Matches:");
            }

            if (entry is DirectoryInfo)
            {
                builder.AppendLine($"[dir]  {relativePath}/ [{matchedTerm}]");
            }
            else if (entry is FileInfo file)
            {
                builder.AppendLine($"[file] {relativePath} [{matchedTerm}] ({file.Length} bytes)");
            }

            matchCount++;

            if (matchCount >= maxMatches)
            {
                truncated = true;
                break;
            }
        }

        if (matchCount == 0)
        {
            builder.AppendLine("Matches: none");
        }

        builder.AppendLine($"Scanned entries: {Math.Min(scannedEntries, MaxSearchFiles)}");
        if (truncated)
        {
            builder.AppendLine($"... truncated after {matchCount} match(es) or {MaxSearchFiles} scanned entries");
        }

        return StoreAndReturn($"{nameof(SearchFiles)} {resolvedPath} ({string.Join(" | ", terms)})", builder.ToString());
    }

    [Description("Summarizes a text file's visible contents and likely purpose without returning the full file. Use this to choose which files need deeper reading.")]
    public async Task<string> SummarizeFilePurpose([Description("The path to the file. Absolute paths are accepted; relative paths resolve from the current working directory.")] string filePath)
    {
        if (!TryReserveToolInvocation(nameof(SummarizeFilePurpose), out string rejectionReason))
        {
            return RejectToolInvocation(nameof(SummarizeFilePurpose), rejectionReason);
        }

        string? resolvedPath = ResolveReadableFilePath(filePath);
        WriteToolCall(nameof(SummarizeFilePurpose),
        [
            ("filePath", filePath),
            ("resolvedPath", resolvedPath ?? "(unresolved)")
        ]);

        if (IsPlaceholderPath(filePath) && resolvedPath is null)
        {
            return StoreAndReturn(nameof(SummarizeFilePurpose), "Error: The file path is a placeholder. Use a real path from the directory listing or attached file header.");
        }

        if (resolvedPath is null || !File.Exists(resolvedPath))
        {
            return StoreAndReturn(nameof(SummarizeFilePurpose), $"Error: File '{filePath}' does not exist. Current working directory: {Environment.CurrentDirectory}");
        }

        if (!TryReadFileContent(resolvedPath, out string content, out string? readError))
        {
            return StoreAndReturn($"{nameof(SummarizeFilePurpose)} {resolvedPath}", $"Error: Could not read file contents: {readError}");
        }

        string extension = Path.GetExtension(resolvedPath);
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var builder = new StringBuilder();
        builder.AppendLine($"File: {resolvedPath}");
        builder.AppendLine($"Size: {content.Length} characters, {lines.Length} lines");
        builder.AppendLine($"Likely purpose: {await InferFilePurpose(resolvedPath, content, CurrentCancellationToken)}");

        string[] declarations = ExtractDeclarations(extension, lines).Take(30).ToArray();
        if (declarations.Length > 0)
        {
            builder.AppendLine("Notable declarations:");
            foreach (string declaration in declarations)
            {
                builder.AppendLine($"- {declaration}");
            }
        }

        string[] imports = ExtractImports(extension, lines).Take(20).ToArray();
        if (imports.Length > 0)
        {
            builder.AppendLine("Imports/references:");
            foreach (string import in imports)
            {
                builder.AppendLine($"- {import}");
            }
        }

        string[] preview = lines
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(12)
            .ToArray();

        if (preview.Length > 0)
        {
            builder.AppendLine("Content preview:");
            foreach (string line in preview)
            {
                builder.AppendLine($"- {Truncate(line, 140)}");
            }
        }

        return StoreAndReturn($"{nameof(SummarizeFilePurpose)} {resolvedPath}", builder.ToString());
    }

    [Description("Executes a shell command after showing it to the user and asking for permission. Uses PowerShell on Windows and Bash on other platforms.")]
    public async Task<string> ExecuteShellCommandAsync(
        [Description("The command to execute. This is passed to PowerShell on Windows and Bash on other platforms.")] string command,
        [Description("Optional working directory for the command. Leave empty to use the current process directory.")] string? workingDirectory = null,
        [Description("Optional timeout in seconds. Defaults to 60 seconds and is capped at 600 seconds.")] int timeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        if (!TryReserveToolInvocation(nameof(ExecuteShellCommandAsync), out string rejectionReason))
        {
            return RejectToolInvocation(nameof(ExecuteShellCommandAsync), rejectionReason);
        }

        WriteToolCall(nameof(ExecuteShellCommandAsync),
        [
            ("command", command),
            ("workingDirectory", workingDirectory ?? Environment.CurrentDirectory),
            ("timeoutSeconds", timeoutSeconds.ToString())
        ]);

        if (string.IsNullOrWhiteSpace(command))
        {
            return StoreAndReturn(nameof(ExecuteShellCommandAsync), "Error: No command was provided.");
        }

        // if (LooksLikeCompoundShellCommand(command))
        // {
        //     return StoreAndReturn($"{nameof(ExecuteShellCommandAsync)} {command}", "Rejected compound shell command. Run exactly one shell operation at a time; do not combine commands with &&, ||, ;, pipes, redirection, or multiple lines.");
        // }

        if (LooksLikeShellFileEditCommand(command))
        {
            return StoreAndReturn($"{nameof(ExecuteShellCommandAsync)} {command}", "Rejected shell-based file edit. Use CreateFileAsync for new files, or read the relevant file and use ApplySearchReplaceAsync with exact SEARCH and REPLACE text.");
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
        {
            return StoreAndReturn(nameof(ExecuteShellCommandAsync), $"Error: Working directory '{workingDirectory}' does not exist.");
        }

        var shell = GetShell();
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, MaxCommandTimeoutSeconds);

        ToolPermissionChoice approval = await RequestPermissionAsync(
            PermissionKey(nameof(ExecuteShellCommandAsync), command),
            "Tool permission requested: execute shell command",
            [
                $"Shell: {shell.DisplayName}",
                $"Working directory: {workingDirectory ?? Environment.CurrentDirectory}",
                "Command:",
                command
            ],
            "Run this command?");
        if (approval == ToolPermissionChoice.Deny)
        {
            WriteCompactToolResult(false, "Command denied", command);
            return StoreAndReturn($"{nameof(ExecuteShellCommandAsync)} {command}", "Command execution denied by user.");
        }

        using var process = new Process();
        process.StartInfo.FileName = shell.FileName;
        foreach (string argument in shell.GetArguments(command))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                outputBuilder.AppendLine(eventArgs.Data);
            }
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                errorBuilder.AppendLine(eventArgs.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            Task waitTask = process.WaitForExitAsync(CurrentCancellationToken);
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), CurrentCancellationToken);

            if (await Task.WhenAny(waitTask, timeoutTask) == timeoutTask)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                return StoreAndReturn($"{nameof(ExecuteShellCommandAsync)} {command}", $"Command timed out after {timeoutSeconds} seconds and was killed.");
            }

            await waitTask;
            WriteCompactToolResult(process.ExitCode == 0, "Command finished", command);
            return StoreAndReturn(
                $"{nameof(ExecuteShellCommandAsync)} {command}",
                FormatCommandResult(process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString()));
        }
        catch (OperationCanceledException) when (CurrentCancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            throw;
        }
        catch (Exception ex)
        {
            return StoreAndReturn($"{nameof(ExecuteShellCommandAsync)} {command}", $"Error executing command: {ex.Message}");
        }
    }

    [Description("Applies a unified diff patch after showing it to the user and asking for permission. Uses git apply --check before applying.")]
    public async Task<string> ApplyDiffPatchAsync(
        [Description("A unified diff patch. It should use git-style file headers such as diff --git, --- a/path, and +++ b/path.")] string patch,
        [Description("Optional working directory where the patch should be applied. Leave empty to use the current process directory.")] string? workingDirectory = null)
    {
        if (!TryReserveToolInvocation(nameof(ApplyDiffPatchAsync), out string rejectionReason))
        {
            return RejectToolInvocation(nameof(ApplyDiffPatchAsync), rejectionReason);
        }

        WriteToolCall(nameof(ApplyDiffPatchAsync),
        [
            ("workingDirectory", workingDirectory ?? Environment.CurrentDirectory),
            ("patchCharacters", patch?.Length.ToString() ?? "0")
        ]);

        if (string.IsNullOrWhiteSpace(patch))
        {
            return StoreAndReturn(nameof(ApplyDiffPatchAsync), "Error: No patch was provided.");
        }

        if (patch.Length > MaxPatchCharacters)
        {
            return StoreAndReturn(nameof(ApplyDiffPatchAsync), $"Error: Patch is too large. Maximum supported patch size is {MaxPatchCharacters} characters.");
        }

        string effectiveWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory;

        if (!Directory.Exists(effectiveWorkingDirectory))
        {
            return StoreAndReturn(nameof(ApplyDiffPatchAsync), $"Error: Working directory '{effectiveWorkingDirectory}' does not exist.");
        }

        ToolPermissionChoice approval = await RequestPermissionAsync(
            PermissionKey(nameof(ApplyDiffPatchAsync), effectiveWorkingDirectory),
            "Tool permission requested: apply diff patch",
            [
                $"Working directory: {effectiveWorkingDirectory}",
                "Patch:",
                patch
            ]);
        if (approval == ToolPermissionChoice.Deny)
        {
            WriteCompactToolResult(false, "Patch denied", effectiveWorkingDirectory);
            return StoreAndReturn(nameof(ApplyDiffPatchAsync), "Patch application denied by user.");
        }

        string patchFile = Path.Combine(Path.GetTempPath(), $"potato-{Guid.NewGuid():N}.patch");

        try
        {
            await File.WriteAllTextAsync(patchFile, patch, CurrentCancellationToken);

            ProcessResult checkResult = await RunProcessAsync(
                "git",
                ["apply", "--check", "--whitespace=nowarn", patchFile],
                effectiveWorkingDirectory,
                DefaultCommandTimeoutSeconds,
                CurrentCancellationToken);

            if (checkResult.ExitCode != 0)
            {
                return StoreAndReturn(nameof(ApplyDiffPatchAsync), FormatProcessResult("Patch validation failed.", checkResult));
            }

            ProcessResult applyResult = await RunProcessAsync(
                "git",
                ["apply", "--whitespace=nowarn", patchFile],
                effectiveWorkingDirectory,
                DefaultCommandTimeoutSeconds,
                CurrentCancellationToken);

            if (applyResult.ExitCode != 0)
            {
                return StoreAndReturn(nameof(ApplyDiffPatchAsync), FormatProcessResult("Patch application failed.", applyResult));
            }

            var builder = new StringBuilder();
            builder.AppendLine("Patch applied successfully.");
            builder.AppendLine("Changed files:");
            builder.AppendLine(FormatPatchedFiles(patch));
            SuccessfulEditCount++;
            WriteCompactToolResult(true, "Patch applied", effectiveWorkingDirectory);
            return StoreAndReturn(nameof(ApplyDiffPatchAsync), builder.ToString());
        }
        catch (OperationCanceledException) when (CurrentCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StoreAndReturn(nameof(ApplyDiffPatchAsync), $"Error applying patch: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(patchFile))
                {
                    File.Delete(patchFile);
                }
            }
            catch
            {
                // Temporary patch cleanup is best-effort.
            }
        }
    }

    [Description("Applies a focused replacement to one file after showing the change to the user and asking for permission. For small edits use exact search. For large edits use unique startAnchor and endAnchor; only the text between them is replaced. As a last option use an inclusive line range.")]
    public async Task<string> ApplySearchReplaceAsync(
        [Description("The path to the file. Absolute paths are accepted; relative paths resolve from the current working directory.")] string filePath,
        [Description("The exact existing text to find in the file. Use for a small exact replacement; leave empty when using anchors or lines.")] string search = "",
        [Description("The replacement text to write in place of the resolved text.")] string replace = "",
        [Description("Unique text immediately before a large replacement. The anchor itself is preserved.")] string? startAnchor = null,
        [Description("Unique text immediately after a large replacement. The anchor itself is preserved.")] string? endAnchor = null,
        [Description("First line to replace, inclusive. Provide together with endLine instead of search or anchors.")] int? startLine = null,
        [Description("Last line to replace, inclusive. Provide together with startLine instead of search or anchors.")] int? endLine = null)
    {
        if (!TryReserveToolInvocation(nameof(ApplySearchReplaceAsync), out string rejectionReason))
        {
            return RejectToolInvocation(nameof(ApplySearchReplaceAsync), rejectionReason);
        }

        string? resolvedPath = ResolveReadableFilePath(filePath);
        WriteToolCall(nameof(ApplySearchReplaceAsync),
        [
            ("filePath", filePath),
            ("resolvedPath", resolvedPath ?? "(unresolved)"),
            ("searchCharacters", search?.Length.ToString() ?? "0"),
            ("replaceCharacters", replace?.Length.ToString() ?? "0"),
            ("startLine", startLine?.ToString() ?? string.Empty),
            ("endLine", endLine?.ToString() ?? string.Empty)
        ]);

        if (IsPlaceholderPath(filePath) && resolvedPath is null)
        {
            return StoreAndReturn(nameof(ApplySearchReplaceAsync), "Error: The file path is a placeholder. Use a real path from the directory listing or attached file header.");
        }

        if (resolvedPath is null || !File.Exists(resolvedPath))
        {
            return StoreAndReturn(nameof(ApplySearchReplaceAsync), $"Error: File '{filePath}' does not exist. Current working directory: {Environment.CurrentDirectory}");
        }

        string content = await File.ReadAllTextAsync(resolvedPath, CurrentCancellationToken);
        if (!TryResolveReplacementSpan(content, search ?? string.Empty, startAnchor, endAnchor, startLine, endLine, out int replacementStart, out int replacementLength, out string resolutionError))
            return StoreAndReturn(nameof(ApplySearchReplaceAsync), resolutionError);

        string resolvedSearch = content.Substring(replacementStart, replacementLength);

        ToolPermissionChoice approval = await RequestPermissionAsync(
            WritePermissionKey(resolvedPath),
            $"WriteFile Writing to {PathResolver.FormatPathForDisplay(resolvedPath)}",
            FormatSearchReplacePreview(resolvedSearch, replace ?? string.Empty));
        if (approval == ToolPermissionChoice.Deny)
        {
            WriteCompactToolResult(false, "WriteFile denied", resolvedPath);
            return StoreAndReturn(nameof(ApplySearchReplaceAsync), "SEARCH/REPLACE edit denied by user.");
        }

        string updatedContent = content.Remove(replacementStart, replacementLength).Insert(replacementStart, replace ?? string.Empty);
        await File.WriteAllTextAsync(resolvedPath, updatedContent, CurrentCancellationToken);
        SuccessfulEditCount++;
        WriteCompactToolResult(true, "WriteFile wrote", resolvedPath);
        return StoreAndReturn(nameof(ApplySearchReplaceAsync), $"SEARCH/REPLACE edit applied successfully to {resolvedPath}.");
    }

    [Description("Uses Fill-in-the-Middle to replace an inclusive line range after showing the generated change and asking for permission. Use this for a focused edit when FIM is available; provide a concise instruction and do not copy the existing file text into the call.")]
    public async Task<string> ApplyFimEditAsync(
        [Description("The path to the existing file.")] string filePath,
        [Description("First line to replace, inclusive.")] int startLine,
        [Description("Last line to replace, inclusive.")] int endLine,
        [Description("Concise description of the desired replacement.")] string instruction)
    {
        if (!TryReserveToolInvocation(nameof(ApplyFimEditAsync), out string rejectionReason))
            return RejectToolInvocation(nameof(ApplyFimEditAsync), rejectionReason);

        string? resolvedPath = ResolveReadableFilePath(filePath);
        WriteToolCall(nameof(ApplyFimEditAsync), [("filePath", filePath), ("resolvedPath", resolvedPath ?? "(unresolved)"), ("startLine", startLine.ToString()), ("endLine", endLine.ToString())]);
        if (resolvedPath is null || !File.Exists(resolvedPath))
            return StoreAndReturn(nameof(ApplyFimEditAsync), $"Error: File '{filePath}' does not exist.");
        if (!await fimClient.IsAvailableAsync(CurrentCancellationToken))
            return StoreAndReturn(nameof(ApplyFimEditAsync), "Error: FIM is unavailable. Use ApplySearchReplaceAsync instead.");

        string content = await File.ReadAllTextAsync(resolvedPath, CurrentCancellationToken);
        string newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (startLine < 1 || endLine < startLine || endLine > lines.Length || endLine - startLine + 1 > MaxFimEditLines)
            return StoreAndReturn(nameof(ApplyFimEditAsync), $"Error: FIM is only reliable for an inclusive range within lines 1-{lines.Length} of at most {MaxFimEditLines} lines. Split a larger edit into focused changes.");

        int prefixStart = Math.Max(0, startLine - 1 - MaxFimContextLines);
        int suffixEnd = Math.Min(lines.Length, endLine + MaxFimContextLines);
        string before = string.Join(newline, lines[prefixStart..(startLine - 1)]);
        string after = string.Join(newline, lines[endLine..suffixEnd]);
        before = TrimFimContextFromStart(before);
        after = TrimFimContextFromEnd(after);
        string prefix = $"{before}{newline}<!-- Potato edit instruction: {instruction.Trim()} -->{newline}";
        int maxCompletionTokens = Math.Clamp((string.Join(newline, lines[(startLine - 1)..endLine]).Length * 2) / 3, 96, 1024);
        string generated = await fimClient.GenerateAsync(chatClientState.Model, prefix, after, maxCompletionTokens, CurrentCancellationToken);
        if (generated.StartsWith("Error:", StringComparison.Ordinal))
            return StoreAndReturn(nameof(ApplyFimEditAsync), generated);
        if (string.IsNullOrWhiteSpace(generated))
            return StoreAndReturn(nameof(ApplyFimEditAsync), "Error: FIM returned an empty replacement.");

        // The instruction is prompt-only; never retain it in the edited file.
        string replacement = generated.Replace("<!-- Potato edit instruction: " + instruction.Trim() + " -->" + newline, string.Empty, StringComparison.Ordinal);
        string search = string.Join(newline, lines[(startLine - 1)..endLine]);
        ToolPermissionChoice approval = await RequestPermissionAsync(WritePermissionKey(resolvedPath),
            $"WriteFile FIM editing {PathResolver.FormatPathForDisplay(resolvedPath)}", FormatSearchReplacePreview(search, replacement));
        if (approval == ToolPermissionChoice.Deny)
        {
            WriteCompactToolResult(false, "WriteFile denied", resolvedPath);
            return StoreAndReturn(nameof(ApplyFimEditAsync), "FIM edit denied by user.");
        }

        string updated = string.Join(newline, lines[..(startLine - 1)]) + (startLine > 1 ? newline : string.Empty) + replacement.TrimEnd('\r', '\n') + (endLine < lines.Length ? newline : string.Empty) + string.Join(newline, lines[endLine..]);
        await File.WriteAllTextAsync(resolvedPath, updated, CurrentCancellationToken);
        SuccessfulEditCount++;
        WriteCompactToolResult(true, "WriteFile wrote", resolvedPath);
        return StoreAndReturn(nameof(ApplyFimEditAsync), $"FIM edit applied successfully to {resolvedPath}.");
    }

    [Description("Creates a new text file after showing the full content to the user and asking for permission. Creates missing parent directories after approval. Fails if the file already exists.")]
    public async Task<string> CreateFileAsync(
        [Description("The path for the new file. Absolute paths are accepted; relative paths resolve from the current working directory.")] string filePath,
        [Description("The full text content to write into the new file. Do not wrap it in Markdown code fences, even when the file extension matches the fence language such as .html and ```html.")] string content)
    {
        if (!TryReserveToolInvocation(nameof(CreateFileAsync), out string rejectionReason))
        {
            return RejectToolInvocation(nameof(CreateFileAsync), rejectionReason);
        }

        string? resolvedPath = ResolveCreatableFilePath(filePath);
        WriteToolCall(nameof(CreateFileAsync),
        [
            ("filePath", filePath),
            ("resolvedPath", resolvedPath ?? "(unresolved)"),
            ("contentCharacters", content?.Length.ToString() ?? "0")
        ]);

        if (IsPlaceholderPath(filePath) || resolvedPath is null)
        {
            return StoreAndReturn(nameof(CreateFileAsync), "Error: The file path is a placeholder. Use a real path from the directory listing or attached file header.");
        }

        if (File.Exists(resolvedPath))
        {
            return StoreAndReturn(nameof(CreateFileAsync), $"Error: File '{resolvedPath}' already exists. Use ApplySearchReplaceAsync to edit existing files.");
        }

        string? parentDirectory = Path.GetDirectoryName(resolvedPath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return StoreAndReturn(nameof(CreateFileAsync), $"Error: Parent directory '{parentDirectory}' could not be resolved.");
        }

        string sanitizedContent = SanitizeGeneratedFileContent(resolvedPath, content);
        bool parentDirectoryExists = Directory.Exists(parentDirectory);
        ToolPermissionChoice approval = await RequestPermissionAsync(
            WritePermissionKey(resolvedPath),
            $"WriteFile Creating {PathResolver.FormatPathForDisplay(resolvedPath)}",
            FormatCreateFilePreview(sanitizedContent, parentDirectoryExists ? null : parentDirectory));
        if (approval == ToolPermissionChoice.Deny)
        {
            WriteCompactToolResult(false, "WriteFile denied", resolvedPath);
            return StoreAndReturn(nameof(CreateFileAsync), "File creation denied by user.");
        }

        Directory.CreateDirectory(parentDirectory);
        await File.WriteAllTextAsync(resolvedPath, sanitizedContent, CurrentCancellationToken);
        SuccessfulEditCount++;
        WriteCompactToolResult(true, "WriteFile created", resolvedPath);
        return StoreAndReturn(nameof(CreateFileAsync), $"File created successfully at {resolvedPath}.");
    }

    [Description("Creates or completely overwrites a text file after showing the full content to the user and asking for permission.")]
    public async Task<string> OverwriteFileAsync(
        [Description("The path for the file. Absolute paths are accepted; relative paths resolve from the current working directory.")] string filePath,
        [Description("The full text content to write into the file. Do not wrap it in Markdown code fences, even when the file extension matches the fence language such as .html and ```html.")] string content)
    {
        if (!TryReserveToolInvocation(nameof(OverwriteFileAsync), out string rejectionReason))
        {
            return RejectToolInvocation(nameof(OverwriteFileAsync), rejectionReason);
        }

        string? resolvedPath = ResolveCreatableFilePath(filePath);
        WriteToolCall(nameof(OverwriteFileAsync),
        [
            ("filePath", filePath),
            ("resolvedPath", resolvedPath ?? "(unresolved)"),
            ("contentCharacters", content?.Length.ToString() ?? "0")
        ]);

        if (IsPlaceholderPath(filePath) || resolvedPath is null)
        {
            return StoreAndReturn(nameof(OverwriteFileAsync), "Error: The file path is a placeholder. Use a real path from the directory listing or attached file header.");
        }

        string? parentDirectory = Path.GetDirectoryName(resolvedPath);
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
        {
            return StoreAndReturn(nameof(OverwriteFileAsync), $"Error: Parent directory '{parentDirectory}' does not exist. Create or choose an existing directory first.");
        }

        string sanitizedContent = SanitizeGeneratedFileContent(resolvedPath, content);
        bool fileExisted = File.Exists(resolvedPath);
        string action = fileExisted ? "Overwriting" : "Creating";
        ToolPermissionChoice approval = await RequestPermissionAsync(
            WritePermissionKey(resolvedPath),
            $"WriteFile {action} {PathResolver.FormatPathForDisplay(resolvedPath)}",
            FormatCreateFilePreview(sanitizedContent));
        if (approval == ToolPermissionChoice.Deny)
        {
            WriteCompactToolResult(false, "WriteFile denied", resolvedPath);
            return StoreAndReturn(nameof(OverwriteFileAsync), "File overwrite denied by user.");
        }

        await File.WriteAllTextAsync(resolvedPath, sanitizedContent, CurrentCancellationToken);
        SuccessfulEditCount++;
        WriteCompactToolResult(true, fileExisted ? "WriteFile wrote" : "WriteFile created", resolvedPath);
        return StoreAndReturn(nameof(OverwriteFileAsync), $"File written successfully at {resolvedPath}.");
    }

    internal static string SanitizeGeneratedFileContent(string filePath, string? content)
    {
        string value = content ?? string.Empty;
        string extension = Path.GetExtension(filePath);
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            return SanitizeHtmlContent(value);
        }

        return StripSingleOuterCodeFence(value);
    }

    private static string SanitizeHtmlContent(string content)
    {
        string value = StripSingleOuterCodeFence(content).Trim();
        int start = IndexOfHtmlDocumentStart(value);
        if (start > 0)
        {
            value = value[start..].TrimStart();
        }

        int end = value.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
        if (end >= 0)
        {
            value = value[..(end + "</html>".Length)].TrimEnd();
        }

        return StripSingleOuterCodeFence(value).Trim();
    }

    private static int IndexOfHtmlDocumentStart(string content)
    {
        int doctype = content.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
        int html = content.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (doctype < 0)
        {
            return html;
        }

        if (html < 0)
        {
            return doctype;
        }

        return Math.Min(doctype, html);
    }

    private static string StripSingleOuterCodeFence(string content)
    {
        string trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return content;
        }

        int firstLineBreak = trimmed.IndexOf('\n', StringComparison.Ordinal);
        int lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstLineBreak < 0 || lastFence <= firstLineBreak)
        {
            return content;
        }

        return trimmed[(firstLineBreak + 1)..lastFence];
    }

    private static bool IsPlaceholderPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return true;
        }

        string normalized = filePath.Trim().Replace('\\', '/').ToLowerInvariant();
        return normalized.Contains("/full/path/", StringComparison.Ordinal) ||
               normalized.Contains("path/to/file", StringComparison.Ordinal) ||
               normalized.Contains("program.cs", StringComparison.Ordinal) && normalized.StartsWith("/full/path", StringComparison.Ordinal);
    }

    private static string? ResolveReadableFilePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        string trimmed = filePath.Trim();
        if (IsPlaceholderPath(trimmed))
        {
            return TryResolvePlaceholderFileName(trimmed);
        }

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

    private static string? ResolveCreatableFilePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        string trimmed = filePath.Trim();
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

    private static string? TryResolvePlaceholderFileName(string filePath)
    {
        string fileName = Path.GetFileName(filePath.Replace('\\', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string candidate = Path.Combine(Environment.CurrentDirectory, fileName);
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    private static string? ResolveReadableDirectoryPath(string? directoryPath)
    {
        string trimmed = string.IsNullOrWhiteSpace(directoryPath)
            ? Environment.CurrentDirectory
            : directoryPath.Trim();

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

    private static IEnumerable<FileSystemInfo> EnumerateFileSystemEntries(string directoryPath, bool recursive)
    {
        var root = new DirectoryInfo(directoryPath);
        if (!root.Exists)
        {
            return [];
        }

        return recursive
            ? EnumerateRecursive(root)
            : root.EnumerateFileSystemInfos()
                .Where(entry => !ShouldSkipDirectory(entry))
                .OrderBy(entry => entry is FileInfo)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<FileSystemInfo> EnumerateRecursive(DirectoryInfo directory)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos()
                     .OrderBy(entry => entry is FileInfo)
                     .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (ShouldSkipDirectory(entry))
            {
                continue;
            }

            yield return entry;

            if (entry is DirectoryInfo childDirectory)
            {
                foreach (FileSystemInfo child in EnumerateRecursive(childDirectory))
                {
                    yield return child;
                }
            }
        }
    }

    private static bool ShouldSkipDirectory(FileSystemInfo entry) =>
        entry is DirectoryInfo &&
        (IsHiddenDirectory(entry) ||
         entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
         entry.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
         entry.Name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
         entry.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase));

    private static bool IsHiddenDirectory(FileSystemInfo entry) =>
        entry is DirectoryInfo &&
        (entry.Name.StartsWith(".", StringComparison.Ordinal) ||
         entry.Attributes.HasFlag(FileAttributes.Hidden));

    private static IEnumerable<FileSystemInfo> EnumerateSearchEntries(string directoryPath, bool recursive)
    {
        var root = new DirectoryInfo(directoryPath);
        if (!root.Exists)
        {
            return [];
        }

        return recursive
            ? EnumerateRecursive(root)
            : root.EnumerateFileSystemInfos()
                .Where(entry => !ShouldSkipDirectory(entry))
                .OrderBy(entry => entry is FileInfo)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<FileInfo> EnumerateSearchFiles(string directoryPath, bool recursive)
    {
        var root = new DirectoryInfo(directoryPath);
        if (!root.Exists)
        {
            return [];
        }

        return recursive
            ? EnumerateRecursive(root).OfType<FileInfo>()
            : root.EnumerateFiles().OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool ShouldSearchFile(FileInfo file) =>
        file.Exists &&
        file.Length <= MaxSearchFileBytes &&
        !SkippedSearchExtensions.Contains(file.Extension);

    private static bool IsProjectManifestFile(FileInfo file)
    {
        string fileName = file.Name;
        string extension = file.Extension;

        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("go.mod", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("pom.xml", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("build.gradle", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase);
    }

    private static string ProjectManifestKind(FileInfo file)
    {
        string fileName = file.Name;
        string extension = file.Extension.ToLowerInvariant();

        return extension switch
        {
            ".sln" => "solution",
            ".csproj" => "csharp-project",
            ".fsproj" => "fsharp-project",
            ".vbproj" => "vb-project",
            _ when fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase) => "node-project",
            _ when fileName.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) => "python-project",
            _ when fileName.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase) => "rust-project",
            _ when fileName.Equals("go.mod", StringComparison.OrdinalIgnoreCase) => "go-module",
            _ when fileName.Equals("pom.xml", StringComparison.OrdinalIgnoreCase) => "maven-project",
            _ when fileName.Equals("build.gradle", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase) => "gradle-project",
            _ => "project"
        };
    }

    private static string[] ParseSearchTerms(string? searchTerms) =>
        (searchTerms ?? string.Empty)
        .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(term => term.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(20)
        .ToArray();

    private static bool LooksBinary(string content) =>
        content.IndexOf('\0', StringComparison.Ordinal) >= 0;

    private static bool FileSystemEntryMatchesTerm(FileSystemInfo entry, string relativePath, string term, StringComparison comparison)
    {
        if (entry.Name.Contains(term, comparison) ||
            relativePath.Contains(term, comparison) ||
            Path.GetExtension(entry.Name).Contains(term, comparison))
        {
            return true;
        }

        string extensionWithoutDot = Path.GetExtension(entry.Name).TrimStart('.');
        string normalizedTerm = term.TrimStart('.');
        return extensionWithoutDot.Length > 0 &&
               extensionWithoutDot.Equals(normalizedTerm, comparison);
    }

    private async Task<string> InferFilePurpose(
        string filePath,
        string content,
        CancellationToken cancellationToken)
    {
        string heuristicPurpose = InferFilePurposeHeuristic(filePath, content);

        if (string.IsNullOrWhiteSpace(content))
        {
            return heuristicPurpose;
        }

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, Potato.Prompts.PromptLibrary.SideQuestionSystemPrompt),
                new(
                    ChatRole.User,
                    Potato.Prompts.PromptLibrary.BuildFilePurposeUserPrompt(
                        filePath,
                        Truncate(content, MaxPurposeInferenceCharacters)))
            };

            ChatResponse response = await chatClientState.OpenAiClient.GetResponseAsync(messages, new ChatOptions(), cancellationToken);
            return string.IsNullOrWhiteSpace(response.Text)
                ? heuristicPurpose
                : response.Text.Trim();
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return heuristicPurpose;
        }
    }

    private static string InferFilePurposeHeuristic(string filePath, string content)
    {
        string fileName = Path.GetFileName(filePath);
        string extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase))
        {
            return "C# application entry point and startup flow.";
        }

        if (extension == ".csproj")
        {
            return "C# project configuration, target framework, and package references.";
        }

        if (extension == ".cs")
        {
            if (content.Contains("static async Task Main", StringComparison.Ordinal) ||
                content.Contains("static void Main", StringComparison.Ordinal))
            {
                return "C# application entry point or startup flow.";
            }

            if (content.Contains("class ", StringComparison.Ordinal) ||
                content.Contains("record ", StringComparison.Ordinal))
            {
                return "C# source file containing application types or behavior.";
            }

            return "C# source file.";
        }

        return extension switch
        {
            ".md" => "Markdown documentation.",
            ".json" => "JSON configuration or structured data.",
            ".http" => "HTTP request examples or API scratch file.",
            ".sln" => "Visual Studio solution file.",
            _ => "Text file."
        };
    }

    private static bool TryReadFileContent(string filePath, out string content, out string? error)
    {
        try
        {
            content = File.ReadAllText(filePath);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            content = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    private static IEnumerable<string> ExtractDeclarations(string extension, string[] lines)
    {
        if (!extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var declarations = new List<string>();
        var typeRegex = new Regex(@"\b(class|interface|record|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
        var methodRegex = new Regex(@"\b(?:public|private|protected|internal|static|async|\s)+[\w<>\[\],?\s]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            Match typeMatch = typeRegex.Match(trimmed);
            if (typeMatch.Success)
            {
                declarations.Add($"{typeMatch.Groups[1].Value} {typeMatch.Groups[2].Value}");
                continue;
            }

            Match methodMatch = methodRegex.Match(trimmed);
            if (methodMatch.Success && !trimmed.StartsWith("if ", StringComparison.Ordinal) && !trimmed.StartsWith("while ", StringComparison.Ordinal))
            {
                declarations.Add($"method {methodMatch.Groups[1].Value}");
            }
        }

        return declarations.Distinct(StringComparer.Ordinal);
    }

    private static IEnumerable<string> ExtractImports(string extension, string[] lines)
    {
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return lines
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("using ", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal);
        }

        return [];
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static bool TryResolveReplacementSpan(
        string content,
        string search,
        string? startAnchor,
        string? endAnchor,
        int? startLine,
        int? endLine,
        out int replacementStart,
        out int replacementLength,
        out string error)
    {
        replacementStart = 0;
        replacementLength = 0;
        error = string.Empty;
        bool hasAnchors = !string.IsNullOrEmpty(startAnchor) || !string.IsNullOrEmpty(endAnchor);
        bool hasLines = startLine.HasValue || endLine.HasValue;
        bool hasSearch = !string.IsNullOrEmpty(search);

        if ((hasAnchors && (string.IsNullOrEmpty(startAnchor) || string.IsNullOrEmpty(endAnchor))) ||
            (hasLines && (!startLine.HasValue || !endLine.HasValue)) ||
            (new[] { hasSearch, hasAnchors, hasLines }.Count(value => value) != 1))
        {
            error = "Error: provide exactly one selector: non-empty search, both startAnchor and endAnchor, or both startLine and endLine.";
            return false;
        }

        if (hasSearch)
        {
            int matchCount = CountOccurrences(content, search);
            if (matchCount == 0)
            {
                error = BuildSearchNotFoundDiagnostic(content, search);
                return false;
            }

            if (matchCount > 1)
            {
                error = $"Error: SEARCH text matched {matchCount} times. Provide a larger unique SEARCH block, or use unique startAnchor and endAnchor.";
                return false;
            }

            replacementStart = content.IndexOf(search, StringComparison.Ordinal);
            replacementLength = search.Length;
            return true;
        }

        if (hasAnchors)
        {
            int startCount = CountOccurrences(content, startAnchor!);
            int endCount = CountOccurrences(content, endAnchor!);
            if (startCount != 1 || endCount != 1)
            {
                error = $"Error: anchors must each be unique. startAnchor matched {startCount} time(s); endAnchor matched {endCount} time(s). Use more surrounding text.";
                return false;
            }

            int startIndex = content.IndexOf(startAnchor!, StringComparison.Ordinal) + startAnchor!.Length;
            int endIndex = content.IndexOf(endAnchor!, StringComparison.Ordinal);
            if (endIndex < startIndex)
            {
                error = "Error: endAnchor occurs before startAnchor. Choose anchors that enclose the intended replacement.";
                return false;
            }

            replacementStart = startIndex;
            replacementLength = endIndex - startIndex;
            return true;
        }

        string newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (startLine < 1 || endLine < startLine || endLine > lines.Length)
        {
            error = $"Error: choose an inclusive range within lines 1-{lines.Length}.";
            return false;
        }

        int firstLine = startLine!.Value;
        int lastLine = endLine!.Value;
        replacementStart = firstLine == 1
            ? 0
            : string.Join(newline, lines[..(firstLine - 1)]).Length + newline.Length;
        replacementLength = string.Join(newline, lines[(firstLine - 1)..lastLine]).Length;
        return true;
    }

    private static string BuildSearchNotFoundDiagnostic(string content, string search)
    {
        string normalizedContent = NormalizeSearchText(content);
        string normalizedSearch = NormalizeSearchText(search);
        if (normalizedContent.Contains(normalizedSearch, StringComparison.Ordinal))
        {
            return "Error: SEARCH text was not found exactly, but it matches after normalizing line endings and trailing whitespace. Re-read the file and use the exact current text.";
        }

        string? anchor = search
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
        if (string.IsNullOrWhiteSpace(anchor))
        {
            return "Error: SEARCH text was not found exactly. The SEARCH block has no non-empty line to use as a diagnostic anchor.";
        }

        int anchorIndex = content.IndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
        {
            return $"Error: SEARCH text was not found exactly, and its first non-empty line was not found: '{Truncate(anchor, 160)}'. Re-read the target file before retrying.";
        }

        int lineNumber = 1 + content[..anchorIndex].Count(character => character == '\n');
        return $"Error: SEARCH text was not found exactly. Its first non-empty line occurs at line {lineNumber}; the rest of the block differs. Re-read lines around {lineNumber} and use an exact unique SEARCH block.";
    }

    private static string NormalizeSearchText(string text) =>
        string.Join('\n', text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd()));

    private static string TrimFimContextFromStart(string value) =>
        value.Length <= MaxFimContextCharacters ? value : value[^MaxFimContextCharacters..];

    private static string TrimFimContextFromEnd(string value) =>
        value.Length <= MaxFimContextCharacters ? value : value[..MaxFimContextCharacters];

    private static bool LooksLikeShellFileEditCommand(string command)
    {
        string normalized = command.ToLowerInvariant();
        return normalized.Contains(">>", StringComparison.Ordinal) ||
               Regex.IsMatch(normalized, @"(^|[^<])>([^>]|$)") ||
               normalized.Contains("sed -i", StringComparison.Ordinal) ||
               normalized.Contains("perl -pi", StringComparison.Ordinal) ||
               normalized.Contains("tee ", StringComparison.Ordinal);
    }

    private static bool LooksLikeCompoundShellCommand(string command)
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

    private void WriteToolCall(string toolName, IReadOnlyList<(string Name, string Value)> parameters)
    {
        using var _ = PotatoConsole.SuspendProgress();
        toolResultWritten = false;

        WriteCompactToolCall(toolName, parameters);
    }

    private static void WriteCompactToolCall(string toolName, IReadOnlyList<(string Name, string Value)> parameters)
    {
        string? path =
            GetParameterValue(parameters, "resolvedFilePath") ??
            GetParameterValue(parameters, "resolvedPath") ??
            GetParameterValue(parameters, "filePath") ??
            GetParameterValue(parameters, "directoryPath");

        string displayPath = string.IsNullOrWhiteSpace(path) || path == "(unresolved)"
            ? string.Empty
            : $" {PathResolver.FormatPathForDisplay(path)}";
        string query = parameters.FirstOrDefault(parameter =>
            parameter.Name.Equals("searchTerms", StringComparison.OrdinalIgnoreCase) ||
            parameter.Name.Equals("query", StringComparison.OrdinalIgnoreCase)).Value;
        string displayQuery = string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : $" query: {Truncate(query, 120)}";
        string? startLine = GetParameterValue(parameters, "startLine") ?? GetParameterValue(parameters, "lineStart");
        string? endLine = GetParameterValue(parameters, "endLine") ?? GetParameterValue(parameters, "lineEnd");
        string displayRange = string.IsNullOrWhiteSpace(startLine) || string.IsNullOrWhiteSpace(endLine)
            ? string.Empty
            : $" lines: {startLine}-{endLine}";

        string label = toolName switch
        {
            nameof(ReadFileContent) => "Read file",
            nameof(ReadFileRange) => "Read file range",
            nameof(ListFiles) => "List files",
            nameof(ListProjectFiles) => "List project files",
            nameof(SearchFiles) => "Search files",
            nameof(SearchFileContents) => "Search file contents",
            nameof(SummarizeFilePurpose) => "Summarize file",
            nameof(ApplySearchReplaceAsync) => "WriteFile",
            nameof(ApplyFimEditAsync) => "WriteFile",
            nameof(CreateFileAsync) => "WriteFile",
            nameof(OverwriteFileAsync) => "WriteFile",
            nameof(ApplyDiffPatchAsync) => "Apply patch",
            nameof(ExecuteShellCommandAsync) => "Execute command",
            _ => toolName
        };
        bool waitsForResult = toolName is nameof(ApplySearchReplaceAsync) or
            nameof(ApplyFimEditAsync) or
            nameof(CreateFileAsync) or
            nameof(OverwriteFileAsync) or
            nameof(ApplyDiffPatchAsync) or
            nameof(ExecuteShellCommandAsync);
        string prefix = waitsForResult ? "?" : "✓";

        Console.ForegroundColor = waitsForResult ? ConsoleColor.DarkGray : ConsoleColor.Green;
        Console.WriteLine($"{prefix} {label}{displayPath}{displayQuery}{displayRange}");
        Console.ResetColor();

        PotatoConsole.EventSink?.Record(
            "tool-call",
            "tool",
            FormatToolEventContent(label, displayPath, displayQuery + displayRange, parameters),
            collapsed: true);
        PotatoConsole.RecordToolActivity("tool-call", FormatToolEventContent(label, displayPath, displayQuery + displayRange, parameters));
    }

    private static string? GetParameterValue(IReadOnlyList<(string Name, string Value)> parameters, string name)
    {
        string? value = parameters.FirstOrDefault(parameter =>
            parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private void WriteCompactToolResult(bool success, string label, string detail)
    {
        using var _ = PotatoConsole.SuspendProgress();
        toolResultWritten = true;

        string displayDetail = string.IsNullOrWhiteSpace(detail)
            ? string.Empty
            : $" {PathResolver.FormatPathForDisplay(detail)}";

        Console.ForegroundColor = success ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"{(success ? "✓" : "x")} {label}{displayDetail}");
        Console.ResetColor();

        PotatoConsole.EventSink?.Record(
            "tool-result",
            "tool",
            $"{(success ? "Success" : "Failed")}: {label}{displayDetail}",
            collapsed: true);
        PotatoConsole.RecordToolActivity("tool-result", $"{(success ? "Success" : "Failed")}: {label}{displayDetail}");
    }

    private static string FormatToolEventContent(
        string label,
        string displayPath,
        string displayQuery,
        IReadOnlyList<(string Name, string Value)> parameters)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{label}{displayPath}{displayQuery}".TrimEnd());
        foreach ((string name, string value) in parameters)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                builder.AppendLine($"{name}: {value}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private string StoreAndReturn(string source, string result)
    {
        if (IsFailureResult(result))
        {
            if (!toolResultWritten)
            {
                WriteCompactToolResult(false, "Tool failed", source);
            }

            WriteToolFailureReason(result);
        }

        memory.Add(source, result);
        return result;
    }

    private static void WriteToolFailureReason(string result)
    {
        string reason = Truncate(FirstLine(result).Trim(), 400);
        if (string.IsNullOrWhiteSpace(reason))
        {
            return;
        }

        using var _ = PotatoConsole.SuspendProgress();
        Console.ForegroundColor = ConsoleColor.DarkRed;
        Console.WriteLine($"  Reason: {reason}");
        Console.ResetColor();

        PotatoConsole.EventSink?.Record("tool-error", "tool", reason, collapsed: false);
    }

    private static bool IsFailureResult(string result)
    {
        string firstLine = FirstLine(result).TrimStart();
        return firstLine.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
               firstLine.StartsWith("Rejected ", StringComparison.OrdinalIgnoreCase) ||
               firstLine.EndsWith(" denied", StringComparison.OrdinalIgnoreCase) ||
               firstLine.EndsWith(" failed.", StringComparison.OrdinalIgnoreCase) ||
               firstLine.Contains(" timed out", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLine(string text)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart();
        int lineEnd = normalized.IndexOf('\n', StringComparison.Ordinal);
        return lineEnd < 0 ? normalized : normalized[..lineEnd];
    }

    private async Task<ToolPermissionChoice> RequestPermissionAsync(
        string permissionKey,
        string title,
        IReadOnlyList<string> details,
        string prompt = "Apply this change?")
    {
        if (options.AlwaysAllowedPermissionKeys.Contains(permissionKey))
        {
            return ToolPermissionChoice.AllowAlways;
        }

        ToolPermissionChoice choice = await PotatoConsole.RequestToolPermissionAsync(permissionKey, title, details, prompt);
        if (choice == ToolPermissionChoice.AllowAlways)
        {
            options.AlwaysAllowedPermissionKeys.Add(permissionKey);
        }

        return choice;
    }

    private static string PermissionKey(string toolName, string scope) =>
        $"{toolName}:{scope}";

    private static string WritePermissionKey(string filePath) =>
        $"write:{Path.GetFullPath(filePath)}";

    private static IReadOnlyList<string> FormatSearchReplacePreview(string search, string replace)
    {
        string[] searchLines = NormalizeLines(search);
        string[] replaceLines = NormalizeLines(replace);
        if (searchLines.Length == 1 && replaceLines.Length == 1)
        {
            if (searchLines[0].Equals(replaceLines[0], StringComparison.Ordinal))
            {
                return ["No line changes."];
            }

            return [$"1 - {searchLines[0]}", $"1 + {replaceLines[0]}"];
        }

        var lines = new List<string>();
        int maxLines = Math.Max(searchLines.Length, replaceLines.Length);
        int changedLines = 0;
        for (int i = 0; i < maxLines; i++)
        {
            string? searchLine = i < searchLines.Length ? searchLines[i] : null;
            string? replaceLine = i < replaceLines.Length ? replaceLines[i] : null;
            if (searchLine is not null &&
                replaceLine is not null &&
                searchLine.Equals(replaceLine, StringComparison.Ordinal))
            {
                continue;
            }

            changedLines++;
            if (changedLines > 20)
            {
                continue;
            }

            if (searchLine is not null)
            {
                lines.Add($"{i + 1} - {searchLine}");
            }

            if (replaceLine is not null)
            {
                lines.Add($"{i + 1} + {replaceLine}");
            }
        }

        if (changedLines == 0)
        {
            return ["No line changes."];
        }

        if (changedLines > 20)
        {
            lines.Add($"... {changedLines - 20} more changed line(s)");
        }

        return lines;
    }

    private static IReadOnlyList<string> FormatCreateFilePreview(string content, string? createdParentDirectory = null)
    {
        string[] lines = NormalizeLines(content);
        var preview = new List<string>();
        if (!string.IsNullOrWhiteSpace(createdParentDirectory))
        {
            preview.Add($"Creates directory: {PathResolver.FormatPathForDisplay(createdParentDirectory)}");
        }

        int visibleLines = Math.Min(lines.Length, 20);
        for (int i = 0; i < visibleLines; i++)
        {
            preview.Add($"{i + 1} + {lines[i]}");
        }

        if (lines.Length > visibleLines)
        {
            preview.Add($"... {lines.Length - visibleLines} more line(s)");
        }

        return preview;
    }

    private static string[] NormalizeLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static ShellCommand GetShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new ShellCommand(
                "PowerShell",
                "powershell.exe",
                command => ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command]);
        }

        string bashPath = File.Exists("/bin/bash") ? "/bin/bash" : "bash";
        return new ShellCommand(
            "Bash",
            bashPath,
            command => ["-lc", command]);
    }

    private static string FormatCommandResult(int exitCode, string standardOutput, string standardError)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Exit code: {exitCode}");
        builder.AppendLine("Stdout:");
        builder.AppendLine(string.IsNullOrWhiteSpace(standardOutput) ? "(empty)" : standardOutput.TrimEnd());
        builder.AppendLine("Stderr:");
        builder.AppendLine(string.IsNullOrWhiteSpace(standardError) ? "(empty)" : standardError.TrimEnd());
        return builder.ToString();
    }

    private static string FormatProcessResult(string title, ProcessResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(title);
        builder.Append(FormatCommandResult(result.ExitCode, result.StandardOutput, result.StandardError));
        return builder.ToString();
    }

    private static string FormatPatchedFiles(string patch)
    {
        string[] files = patch
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal) ||
                           line.StartsWith("--- a/", StringComparison.Ordinal))
            .Select(line => line[6..].Trim())
            .Where(path => path != "/dev/null")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return files.Length == 0 ? "(none found in patch headers)" : string.Join(Environment.NewLine, files);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string[] arguments,
        string workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                outputBuilder.AppendLine(eventArgs.Data);
            }
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                errorBuilder.AppendLine(eventArgs.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            Task waitTask = process.WaitForExitAsync(cancellationToken);
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);

            if (await Task.WhenAny(waitTask, timeoutTask) == timeoutTask)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                return new ProcessResult(
                    -1,
                    outputBuilder.ToString(),
                    $"Process timed out after {timeoutSeconds} seconds and was killed.\n{errorBuilder}");
            }

            await waitTask;
            return new ProcessResult(process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            throw;
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process may already have exited while cancellation was being handled.
        }
    }

    private sealed record ShellCommand(
        string DisplayName,
        string FileName,
        Func<string, string[]> GetArguments);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
