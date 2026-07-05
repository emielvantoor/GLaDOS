using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal class AgentTools(ReActMemory memory)
{
    private const int DefaultCommandTimeoutSeconds = 60;
    private const int MaxCommandTimeoutSeconds = 600;
    private const int MaxPatchCharacters = 200_000;

    public int ToolInvocationCount { get; private set; }

    [Description("Gets the current local system date and time.")]
    public string GetCurrentTime()
    {
        ToolInvocationCount++;
        WriteToolCall(nameof(GetCurrentTime), []);
        string result = $"The current local time is: {DateTime.Now:F}";
        memory.Add(nameof(GetCurrentTime), result);
        return result;
    }

    [Description("Reads the contents of a specific text file from disk.")]
    public string ReadFileContent([Description("The path to the file. Absolute paths are accepted; relative paths resolve from the current working directory.")] string filePath)
    {
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

    [Description("Gets collected ReAct context by index. Use index 'list' to list available items with descriptions, 'latest' for the newest item, or a numeric index. Set full to true only when exact full content is needed.")]
    public string GetCollectedContext(
        [Description("Use 'list', 'latest', or a numeric index from the collected context list.")] string index = "list",
        [Description("Whether to return full stored content instead of a summary when available.")] bool full = false)
    {
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

        if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
        {
            return StoreAndReturn(nameof(ExecuteShellCommandAsync), $"Error: Working directory '{workingDirectory}' does not exist.");
        }

        var shell = GetShell();
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, MaxCommandTimeoutSeconds);

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("Tool permission requested: execute shell command");
        Console.WriteLine($"Shell: {shell.DisplayName}");
        Console.WriteLine($"Working directory: {workingDirectory ?? Environment.CurrentDirectory}");
        Console.WriteLine("Command:");
        Console.WriteLine(command);
        Console.ResetColor();
        Console.Write("Allow execution? [y/N] ");

        string? approval = Console.ReadLine();
        if (!IsApproval(approval))
        {
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

            Task waitTask = process.WaitForExitAsync();
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));

            if (await Task.WhenAny(waitTask, timeoutTask) == timeoutTask)
            {
                process.Kill(entireProcessTree: true);
                await waitTask;
                return StoreAndReturn($"{nameof(ExecuteShellCommandAsync)} {command}", $"Command timed out after {timeoutSeconds} seconds and was killed.");
            }

            return StoreAndReturn(
                $"{nameof(ExecuteShellCommandAsync)} {command}",
                FormatCommandResult(process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString()));
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

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("Tool permission requested: apply diff patch");
        Console.WriteLine($"Working directory: {effectiveWorkingDirectory}");
        Console.WriteLine("Patch:");
        Console.WriteLine(patch);
        Console.ResetColor();
        Console.Write("Apply patch? [y/N] ");

        string? approval = Console.ReadLine();
        if (!IsApproval(approval))
        {
            return StoreAndReturn(nameof(ApplyDiffPatchAsync), "Patch application denied by user.");
        }

        string patchFile = Path.Combine(Path.GetTempPath(), $"potato-{Guid.NewGuid():N}.patch");

        try
        {
            await File.WriteAllTextAsync(patchFile, patch);

            ProcessResult checkResult = await RunProcessAsync(
                "git",
                ["apply", "--check", "--whitespace=nowarn", patchFile],
                effectiveWorkingDirectory,
                DefaultCommandTimeoutSeconds);

            if (checkResult.ExitCode != 0)
            {
                return StoreAndReturn(nameof(ApplyDiffPatchAsync), FormatProcessResult("Patch validation failed.", checkResult));
            }

            ProcessResult applyResult = await RunProcessAsync(
                "git",
                ["apply", "--whitespace=nowarn", patchFile],
                effectiveWorkingDirectory,
                DefaultCommandTimeoutSeconds);

            if (applyResult.ExitCode != 0)
            {
                return StoreAndReturn(nameof(ApplyDiffPatchAsync), FormatProcessResult("Patch application failed.", applyResult));
            }

            var builder = new StringBuilder();
            builder.AppendLine("Patch applied successfully.");
            builder.AppendLine("Changed files:");
            builder.AppendLine(FormatPatchedFiles(patch));
            return StoreAndReturn(nameof(ApplyDiffPatchAsync), builder.ToString());
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

    private static void WriteToolCall(string toolName, IReadOnlyList<(string Name, string Value)> parameters)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"Tool call: {toolName}");
        foreach ((string name, string value) in parameters)
        {
            Console.WriteLine($"  {name}: {value}");
        }

        Console.ResetColor();
    }

    private string StoreAndReturn(string source, string result)
    {
        memory.Add(source, result);
        return result;
    }

    private static bool IsApproval(string? input)
    {
        string normalized = input?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is "y" or "yes";
    }

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
        int timeoutSeconds)
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

        Task waitTask = process.WaitForExitAsync();
        Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));

        if (await Task.WhenAny(waitTask, timeoutTask) == timeoutTask)
        {
            process.Kill(entireProcessTree: true);
            await waitTask;
            return new ProcessResult(
                -1,
                outputBuilder.ToString(),
                $"Process timed out after {timeoutSeconds} seconds and was killed.\n{errorBuilder}");
        }

        return new ProcessResult(process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
    }

    private sealed record ShellCommand(
        string DisplayName,
        string FileName,
        Func<string, string[]> GetArguments);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
