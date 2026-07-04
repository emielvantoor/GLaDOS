using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public class AgentTools
{
    private const int DefaultCommandTimeoutSeconds = 60;
    private const int MaxCommandTimeoutSeconds = 600;

    [Description("Gets the current local system date and time.")]
    public string GetCurrentTime() => $"The current local time is: {DateTime.Now:F}";

    [Description("Reads the contents of a specific text file from disk.")]
    public string ReadFileContent([Description("The full path to the file.")] string filePath)
    {
        if (IsPlaceholderPath(filePath))
        {
            return "Error: The file path is a placeholder. Use the exact absolute path from the user request or attached file header.";
        }

        if (!File.Exists(filePath))
        {
            return $"Error: File '{filePath}' does not exist.";
        }

        return File.ReadAllText(filePath);
    }

    [Description("Executes a shell command after showing it to the user and asking for permission. Uses PowerShell on Windows and Bash on other platforms.")]
    public async Task<string> ExecuteShellCommandAsync(
        [Description("The command to execute. This is passed to PowerShell on Windows and Bash on other platforms.")] string command,
        [Description("Optional working directory for the command. Leave empty to use the current process directory.")] string? workingDirectory = null,
        [Description("Optional timeout in seconds. Defaults to 60 seconds and is capped at 600 seconds.")] int timeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "Error: No command was provided.";
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
        {
            return $"Error: Working directory '{workingDirectory}' does not exist.";
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
            return "Command execution denied by user.";
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
                return $"Command timed out after {timeoutSeconds} seconds and was killed.";
            }

            return FormatCommandResult(process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
        }
        catch (Exception ex)
        {
            return $"Error executing command: {ex.Message}";
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

    private sealed record ShellCommand(
        string DisplayName,
        string FileName,
        Func<string, string[]> GetArguments);
}
