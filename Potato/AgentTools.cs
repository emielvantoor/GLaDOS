using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

internal class AgentTools(ReActMemory memory, Func<IChatClient> getSideQuestionClient, PotatoRuntimeOptions options)
{
    private const int DefaultCommandTimeoutSeconds = 60;
    private const int MaxCommandTimeoutSeconds = 600;
    private const int MaxPatchCharacters = 200_000;
    private const int MaxPurposeInferenceCharacters = 12_000;

    public int ToolInvocationCount { get; private set; }

    public int SuccessfulEditCount { get; private set; }

    public CancellationToken CurrentCancellationToken { get; set; }

    private bool toolResultWritten;

    [Description("Gets the current local system date and time.")]
    public string GetCurrentTime()
    {
        CurrentCancellationToken.ThrowIfCancellationRequested();
        ToolInvocationCount++;
        WriteToolCall(nameof(GetCurrentTime), []);
        string result = $"The current local time is: {DateTime.Now:F}";
        memory.Add(nameof(GetCurrentTime), result);
        return result;
    }

    [Description("Reads the contents of a specific text file from disk.")]
    public string ReadFileContent([Description("The path to the file. Absolute paths are accepted; relative paths resolve from the current working directory.")] string filePath)
    {
        CurrentCancellationToken.ThrowIfCancellationRequested();
        ToolInvocationCount++;
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

    [Description("Lists files and directories without using the shell. Use this for read-only project discovery before choosing files to inspect.")]
    public string ListFiles(
        [Description("Optional directory path. Absolute paths are accepted; relative paths resolve from the current working directory. Leave empty to list the current working directory.")] string? directoryPath = null,
        [Description("Whether to recurse into subdirectories. Defaults to false.")] bool recursive = false,
        [Description("Maximum number of entries to return. Defaults to 200 and is capped at 1000.")] int maxEntries = 200)
    {
        CurrentCancellationToken.ThrowIfCancellationRequested();
        ToolInvocationCount++;
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

        return StoreAndReturn($"{nameof(ListFiles)} {resolvedPath}", builder.ToString());
    }

    [Description("Summarizes a text file's visible contents and likely purpose without returning the full file. Use this to choose which files need deeper reading.")]
    public async Task<string> SummarizeFilePurpose([Description("The path to the file. Absolute paths are accepted; relative paths resolve from the current working directory.")] string filePath)
    {
        CurrentCancellationToken.ThrowIfCancellationRequested();
        ToolInvocationCount++;
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

    [Description("Gets collected ReAct context by index. Use index 'list' to list available items with descriptions, 'latest' for the newest item, or a numeric index. Set full to true only when exact full content is needed.")]
    public string GetCollectedContext(
        [Description("Use 'list', 'latest', or a numeric index from the collected context list.")] string index = "list",
        [Description("Whether to return full stored content instead of a summary when available.")] bool full = false)
    {
        CurrentCancellationToken.ThrowIfCancellationRequested();
        ToolInvocationCount++;
        WriteToolCall(nameof(GetCollectedContext),
        [
            ("index", index),
            ("full", full.ToString())
        ]);

        return memory.Get(index, full);
    }

    [Description("Executes a shell command after showing it to the user and asking for permission. Uses PowerShell on Windows and Bash on other platforms.")]
    public async Task<string> ExecuteShellCommandAsync(
        [Description("The command to execute. This is passed to PowerShell on Windows and Bash on other platforms.")] string command,
        [Description("Optional working directory for the command. Leave empty to use the current process directory.")] string? workingDirectory = null,
        [Description("Optional timeout in seconds. Defaults to 60 seconds and is capped at 600 seconds.")] int timeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        CurrentCancellationToken.ThrowIfCancellationRequested();
        ToolInvocationCount++;
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

        if (LooksLikeDirectoryListingCommand(command))
        {
            return StoreAndReturn($"{nameof(ExecuteShellCommandAsync)} {command}", "Rejected shell directory listing. Use the ListFiles tool instead.");
        }

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

        ToolPermissionChoice approval = RequestPermission(
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
        CurrentCancellationToken.ThrowIfCancellationRequested();
        ToolInvocationCount++;
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

        ToolPermissionChoice approval = RequestPermission(
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

    [Description("Applies an exact Qwen/Aider-style SEARCH/REPLACE edit to one file after showing the change to the user and asking for permission.")]
    public async Task<string> ApplySearchReplaceAsync(
        [Description("The path to the file. Absolute paths are accepted; relative paths resolve from the current working directory.")] string filePath,
        [Description("The exact existing text to find in the file.")] string search,
        [Description("The replacement text to write in place of the search text.")] string replace)
    {
        CurrentCancellationToken.ThrowIfCancellationRequested();
        ToolInvocationCount++;
        string? resolvedPath = ResolveReadableFilePath(filePath);
        WriteToolCall(nameof(ApplySearchReplaceAsync),
        [
            ("filePath", filePath),
            ("resolvedPath", resolvedPath ?? "(unresolved)"),
            ("searchCharacters", search?.Length.ToString() ?? "0"),
            ("replaceCharacters", replace?.Length.ToString() ?? "0")
        ]);

        if (IsPlaceholderPath(filePath) && resolvedPath is null)
        {
            return StoreAndReturn(nameof(ApplySearchReplaceAsync), "Error: The file path is a placeholder. Use a real path from the directory listing or attached file header.");
        }

        if (resolvedPath is null || !File.Exists(resolvedPath))
        {
            return StoreAndReturn(nameof(ApplySearchReplaceAsync), $"Error: File '{filePath}' does not exist. Current working directory: {Environment.CurrentDirectory}");
        }

        if (string.IsNullOrEmpty(search))
        {
            return StoreAndReturn(nameof(ApplySearchReplaceAsync), "Error: SEARCH text cannot be empty.");
        }

        string content = await File.ReadAllTextAsync(resolvedPath, CurrentCancellationToken);
        int matchCount = CountOccurrences(content, search);
        if (matchCount == 0)
        {
            return StoreAndReturn(nameof(ApplySearchReplaceAsync), "Error: SEARCH text was not found exactly in the target file.");
        }

        if (matchCount > 1)
        {
            return StoreAndReturn(nameof(ApplySearchReplaceAsync), $"Error: SEARCH text matched {matchCount} times. Provide a larger unique SEARCH block.");
        }

        ToolPermissionChoice approval = RequestPermission(
            PermissionKey(nameof(ApplySearchReplaceAsync), resolvedPath),
            $"WriteFile Writing to {PathResolver.FormatPathForDisplay(resolvedPath)}",
            FormatSearchReplacePreview(search ?? string.Empty, replace ?? string.Empty));
        if (approval == ToolPermissionChoice.Deny)
        {
            WriteCompactToolResult(false, "WriteFile denied", resolvedPath);
            return StoreAndReturn(nameof(ApplySearchReplaceAsync), "SEARCH/REPLACE edit denied by user.");
        }

        string updatedContent = content.Replace(search!, replace ?? string.Empty, StringComparison.Ordinal);
        await File.WriteAllTextAsync(resolvedPath, updatedContent, CurrentCancellationToken);
        SuccessfulEditCount++;
        WriteCompactToolResult(true, "WriteFile wrote", resolvedPath);
        return StoreAndReturn(nameof(ApplySearchReplaceAsync), $"SEARCH/REPLACE edit applied successfully to {resolvedPath}.");
    }

    [Description("Creates a new text file after showing the full content to the user and asking for permission. Fails if the file already exists.")]
    public async Task<string> CreateFileAsync(
        [Description("The path for the new file. Absolute paths are accepted; relative paths resolve from the current working directory.")] string filePath,
        [Description("The full text content to write into the new file.")] string content)
    {
        CurrentCancellationToken.ThrowIfCancellationRequested();
        ToolInvocationCount++;
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
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
        {
            return StoreAndReturn(nameof(CreateFileAsync), $"Error: Parent directory '{parentDirectory}' does not exist. Create or choose an existing directory first.");
        }

        ToolPermissionChoice approval = RequestPermission(
            PermissionKey(nameof(CreateFileAsync), resolvedPath),
            $"WriteFile Creating {PathResolver.FormatPathForDisplay(resolvedPath)}",
            FormatCreateFilePreview(content ?? string.Empty));
        if (approval == ToolPermissionChoice.Deny)
        {
            WriteCompactToolResult(false, "WriteFile denied", resolvedPath);
            return StoreAndReturn(nameof(CreateFileAsync), "File creation denied by user.");
        }

        await File.WriteAllTextAsync(resolvedPath, content, CurrentCancellationToken);
        SuccessfulEditCount++;
        WriteCompactToolResult(true, "WriteFile created", resolvedPath);
        return StoreAndReturn(nameof(CreateFileAsync), $"File created successfully at {resolvedPath}.");
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
        (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
         entry.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
         entry.Name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
         entry.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase));

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
                new(ChatRole.System, PromptLibrary.SideQuestionSystemPrompt),
                new(
                    ChatRole.User,
                    "Summarize this file's purpose and likely use case. " +
                    "Use the file path and visible contents when possible. " +
                    "Return one concise sentence and do not quote large snippets.\n\n" +
                    $"File path: {filePath}\n\n" +
                    "File contents:\n" +
                    Truncate(content, MaxPurposeInferenceCharacters))
            };

            ChatResponse response = await getSideQuestionClient().GetResponseAsync(messages, new ChatOptions(), cancellationToken);
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

    private static bool LooksLikeDirectoryListingCommand(string command)
    {
        string normalized = command.TrimStart().ToLowerInvariant();
        return normalized.StartsWith("ls", StringComparison.Ordinal) ||
               normalized.StartsWith("dir", StringComparison.Ordinal) ||
               normalized.StartsWith("tree", StringComparison.Ordinal);
    }

    private static bool LooksLikeShellFileEditCommand(string command)
    {
        string normalized = command.ToLowerInvariant();
        return normalized.Contains(">>", StringComparison.Ordinal) ||
               Regex.IsMatch(normalized, @"(^|[^<])>([^>]|$)") ||
               normalized.Contains("sed -i", StringComparison.Ordinal) ||
               normalized.Contains("perl -pi", StringComparison.Ordinal) ||
               normalized.Contains("tee ", StringComparison.Ordinal);
    }

    private void WriteToolCall(string toolName, IReadOnlyList<(string Name, string Value)> parameters)
    {
        using var _ = PotatoConsole.SuspendProgress();
        toolResultWritten = false;

        if (!options.Verbose)
        {
            WriteCompactToolCall(toolName, parameters);
            return;
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"Tool call: {toolName}");
        foreach ((string name, string value) in parameters)
        {
            Console.WriteLine($"  {name}: {value}");
        }

        Console.ResetColor();
    }

    private static void WriteCompactToolCall(string toolName, IReadOnlyList<(string Name, string Value)> parameters)
    {
        string? path = parameters.FirstOrDefault(parameter =>
            parameter.Name.Equals("resolvedPath", StringComparison.OrdinalIgnoreCase) ||
            parameter.Name.Equals("filePath", StringComparison.OrdinalIgnoreCase) ||
            parameter.Name.Equals("directoryPath", StringComparison.OrdinalIgnoreCase)).Value;

        string displayPath = string.IsNullOrWhiteSpace(path) || path == "(unresolved)"
            ? string.Empty
            : $" {PathResolver.FormatPathForDisplay(path)}";

        string label = toolName switch
        {
            nameof(ReadFileContent) => "Read file",
            nameof(ListFiles) => "List files",
            nameof(SummarizeFilePurpose) => "Summarize file",
            nameof(GetCollectedContext) => "Read context",
            nameof(ApplySearchReplaceAsync) => "WriteFile",
            nameof(CreateFileAsync) => "WriteFile",
            nameof(ApplyDiffPatchAsync) => "Apply patch",
            nameof(ExecuteShellCommandAsync) => "Execute command",
            _ => toolName
        };
        bool waitsForResult = toolName is nameof(ApplySearchReplaceAsync) or
            nameof(CreateFileAsync) or
            nameof(ApplyDiffPatchAsync) or
            nameof(ExecuteShellCommandAsync);
        string prefix = waitsForResult ? "?" : "✓";

        Console.ForegroundColor = waitsForResult ? ConsoleColor.DarkGray : ConsoleColor.Green;
        Console.WriteLine($"{prefix} {label}{displayPath}");
        Console.ResetColor();
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
    }

    private string StoreAndReturn(string source, string result)
    {
        if (!toolResultWritten && IsFailureResult(result))
        {
            WriteCompactToolResult(false, "Tool failed", source);
        }

        memory.Add(source, result);
        return result;
    }

    private static bool IsFailureResult(string result)
    {
        string trimmed = result.TrimStart();
        return trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("Rejected ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains(" denied", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains(" failed", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains(" timed out", StringComparison.OrdinalIgnoreCase);
    }

    private ToolPermissionChoice RequestPermission(
        string permissionKey,
        string title,
        IReadOnlyList<string> details,
        string prompt = "Apply this change?")
    {
        if (options.AlwaysAllowedPermissionKeys.Contains(permissionKey))
        {
            return ToolPermissionChoice.AllowAlways;
        }

        ToolPermissionChoice choice = PotatoConsole.RequestToolPermission(title, details, prompt);
        if (choice == ToolPermissionChoice.AllowAlways)
        {
            options.AlwaysAllowedPermissionKeys.Add(permissionKey);
        }

        return choice;
    }

    private static string PermissionKey(string toolName, string scope) =>
        $"{toolName}:{scope}";

    private static IReadOnlyList<string> FormatSearchReplacePreview(string search, string replace)
    {
        string[] searchLines = NormalizeLines(search);
        string[] replaceLines = NormalizeLines(replace);
        if (searchLines.Length == 1 && replaceLines.Length == 1)
        {
            return [$"1 - {searchLines[0]}", $"1 + {replaceLines[0]}"];
        }

        var lines = new List<string>();
        int maxLines = Math.Max(searchLines.Length, replaceLines.Length);
        int visibleLines = Math.Min(maxLines, 20);
        for (int i = 0; i < visibleLines; i++)
        {
            if (i < searchLines.Length)
            {
                lines.Add($"{i + 1} - {searchLines[i]}");
            }

            if (i < replaceLines.Length)
            {
                lines.Add($"{i + 1} + {replaceLines[i]}");
            }
        }

        if (maxLines > visibleLines)
        {
            lines.Add($"... {maxLines - visibleLines} more changed line(s)");
        }

        return lines;
    }

    private static IReadOnlyList<string> FormatCreateFilePreview(string content)
    {
        string[] lines = NormalizeLines(content);
        var preview = new List<string>();
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
