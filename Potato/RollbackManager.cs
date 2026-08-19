using System.Security.Cryptography;
using System.Text;

namespace Potato;

/// <summary>
/// Keeps in-memory checkpoints for files changed by Potato. A rollback only
/// proceeds when the file still matches the version Potato wrote, preventing a
/// rollback from silently overwriting intervening user changes.
/// </summary>
public sealed class RollbackManager
{
    private readonly List<RollbackCheckpoint> checkpoints = [];
    private readonly List<RollbackCheckpoint> taskCheckpoints = [];
    private ActiveTaskCheckpoint? activeTask;

    public IReadOnlyList<FileSnapshot> CaptureFiles(IEnumerable<string> paths) => paths
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(CaptureFile)
        .ToArray();

    public FileSnapshot CaptureFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? new FileSnapshot(fullPath, true, File.ReadAllBytes(fullPath), null)
            : new FileSnapshot(fullPath, false, null, null);
    }

    public void Record(string description, IReadOnlyList<FileSnapshot> before)
    {
        if (before.Count == 0) return;

        FileSnapshot[] completed = before.Select(snapshot =>
        {
            bool exists = File.Exists(snapshot.Path);
            byte[]? content = exists ? File.ReadAllBytes(snapshot.Path) : null;
            return snapshot with { ExpectedAfterHash = Hash(content) };
        }).ToArray();

        checkpoints.Add(new RollbackCheckpoint(checkpoints.Count + 1, description, DateTimeOffset.Now, completed, []));
        activeTask?.Record(completed);
        PotatoConsole.WriteStatus($"Checkpoint {checkpoints[^1].Number} created for {completed.Length} file(s).");
    }

    public void BeginTask(string description)
    {
        if (activeTask is not null)
        {
            CompleteTask("Interrupted Potato task");
        }

        activeTask = new ActiveTaskCheckpoint(description);
    }

    public string? CompleteTask(string? description = null)
    {
        if (activeTask is null || activeTask.Files.Count == 0)
        {
            activeTask = null;
            return null;
        }

        int number = taskCheckpoints.Count + 1;
        string taskDescription = string.IsNullOrWhiteSpace(description) ? activeTask.Description : description;
        FileSnapshot[] files = activeTask.Files.Values.ToArray();
        ChangedFileSummary[] changes = files.Select(CreateChangeSummary).ToArray();
        taskCheckpoints.Add(new RollbackCheckpoint(number, taskDescription, DateTimeOffset.Now, files, changes));
        activeTask = null;
        return FormatTaskCheckpointReady(taskCheckpoints[^1]);
    }

    public string List()
    {
        if (checkpoints.Count == 0)
        {
            return "No Potato checkpoints are available in this session.";
        }

        var builder = new StringBuilder("Potato checkpoints:\n");
        foreach (RollbackCheckpoint checkpoint in checkpoints)
        {
            builder.AppendLine($"  {checkpoint.Number}: {checkpoint.Description} ({checkpoint.CreatedAt:HH:mm:ss})");
            foreach (FileSnapshot file in checkpoint.Files)
            {
                builder.AppendLine($"     {PathResolver.FormatPathForDisplay(file.Path)}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    public string ListTaskCheckpoints()
    {
        if (taskCheckpoints.Count == 0)
        {
            return "No completed Potato task checkpoints are available in this session.";
        }

        var builder = new StringBuilder("Potato task checkpoints:\n");
        foreach (RollbackCheckpoint checkpoint in taskCheckpoints)
        {
            builder.AppendLine($"  {checkpoint.Number}: {checkpoint.Description} ({checkpoint.CreatedAt:HH:mm:ss})");
            foreach (ChangedFileSummary file in checkpoint.Changes)
            {
                builder.AppendLine($"     {PathResolver.FormatPathForDisplay(file.Path)} +{file.Added} -{file.Removed}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    public async Task<string> RollbackAsync(string selector, CancellationToken cancellationToken = default)
    {
        RollbackCheckpoint? checkpoint = Resolve(selector);
        return await RollbackCheckpointAsync(checkpoint, "checkpoint", cancellationToken);
    }

    public async Task<string> RollbackTaskAsync(string selector, CancellationToken cancellationToken = default)
    {
        RollbackCheckpoint? checkpoint = ResolveTask(selector);
        return await RollbackCheckpointAsync(checkpoint, "task checkpoint", cancellationToken);
    }

    private async Task<string> RollbackCheckpointAsync(
        RollbackCheckpoint? checkpoint,
        string checkpointKind,
        CancellationToken cancellationToken)
    {
        if (checkpoint is null)
        {
            return checkpointKind == "task checkpoint"
                ? "No matching Potato task checkpoint. Use /task-checkpoints to list available checkpoints."
                : "No matching Potato checkpoint. Use /checkpoints to list available checkpoints.";
        }

        string[] conflicts = checkpoint.Files
            .Where(file => !string.Equals(Hash(File.Exists(file.Path) ? File.ReadAllBytes(file.Path) : null), file.ExpectedAfterHash, StringComparison.Ordinal))
            .Select(file => PathResolver.FormatPathForDisplay(file.Path))
            .ToArray();
        if (conflicts.Length > 0)
        {
            return $"Rollback refused: these files changed after {checkpointKind} {checkpoint.Number}: {string.Join(", ", conflicts)}. Create a new checkpoint or resolve the changes manually.";
        }

        ToolPermissionChoice approval = await PotatoConsole.RequestToolPermissionAsync(
            $"rollback:{checkpointKind}:{checkpoint.Number}",
            $"Rollback Potato {checkpointKind} {checkpoint.Number}",
            [
                checkpoint.Description,
                "Files to restore:",
                .. checkpoint.Files.Select(file => PathResolver.FormatPathForDisplay(file.Path))
            ],
            "Restore these files to their state before this Potato action?");
        if (approval == ToolPermissionChoice.Deny)
        {
            return $"Rollback of {checkpointKind} {checkpoint.Number} denied by user.";
        }

        foreach (FileSnapshot file in checkpoint.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.ExistedBefore)
            {
                string? directory = Path.GetDirectoryName(file.Path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(file.Path, file.ContentBefore!, cancellationToken);
            }
            else if (File.Exists(file.Path))
            {
                File.Delete(file.Path);
            }
        }

        if (checkpointKind == "task checkpoint") taskCheckpoints.Remove(checkpoint);
        else checkpoints.Remove(checkpoint);
        return $"Rolled back {checkpointKind} {checkpoint.Number}: {checkpoint.Description}";
    }

    private RollbackCheckpoint? Resolve(string selector)
    {
        string value = selector.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return checkpoints.LastOrDefault();
        }

        return int.TryParse(value, out int number)
            ? checkpoints.FirstOrDefault(checkpoint => checkpoint.Number == number)
            : null;
    }

    private RollbackCheckpoint? ResolveTask(string selector)
    {
        string value = selector.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return taskCheckpoints.LastOrDefault();
        }

        return int.TryParse(value, out int number)
            ? taskCheckpoints.FirstOrDefault(checkpoint => checkpoint.Number == number)
            : null;
    }

    private static string Hash(byte[]? content) =>
        content is null ? "<missing>" : Convert.ToHexString(SHA256.HashData(content));

    private static string FormatTaskCheckpointReady(RollbackCheckpoint checkpoint)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Task checkpoint {checkpoint.Number} is ready. Use /rollback-task {checkpoint.Number} to undo this completed task.");
        builder.AppendLine("Changed Files");
        foreach (ChangedFileSummary change in checkpoint.Changes)
        {
            builder.AppendLine($"{PathResolver.FormatPathForDisplay(change.Path)} +{change.Added} -{change.Removed}");
        }

        return builder.ToString().TrimEnd();
    }

    private static ChangedFileSummary CreateChangeSummary(FileSnapshot before)
    {
        byte[]? after = File.Exists(before.Path) ? File.ReadAllBytes(before.Path) : null;
        (int added, int removed) = CountChangedLines(before.ContentBefore, after);
        return new ChangedFileSummary(before.Path, added, removed);
    }

    private static (int Added, int Removed) CountChangedLines(byte[]? before, byte[]? after)
    {
        string[] oldLines = SplitLines(before);
        string[] newLines = SplitLines(after);
        if (oldLines.Length == 0) return (newLines.Length, 0);
        if (newLines.Length == 0) return (0, oldLines.Length);

        // Exact line statistics for normal source files. For unusually large files,
        // avoid quadratic work and report the changed middle after removing shared
        // prefix/suffix lines.
        if ((long)oldLines.Length * newLines.Length > 4_000_000)
        {
            int prefix = 0;
            while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
            int suffix = 0;
            while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix &&
                   oldLines[^(suffix + 1)] == newLines[^(suffix + 1)]) suffix++;
            return (newLines.Length - prefix - suffix, oldLines.Length - prefix - suffix);
        }

        int[] previous = new int[newLines.Length + 1];
        int[] current = new int[newLines.Length + 1];
        foreach (string oldLine in oldLines)
        {
            for (int index = 1; index <= newLines.Length; index++)
            {
                current[index] = oldLine == newLines[index - 1]
                    ? previous[index - 1] + 1
                    : Math.Max(previous[index], current[index - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        int commonLines = previous[^1];
        return (newLines.Length - commonLines, oldLines.Length - commonLines);
    }

    private static string[] SplitLines(byte[]? content)
    {
        if (content is null || content.Length == 0) return [];
        string text = Encoding.UTF8.GetString(content).Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = text.Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    public sealed record FileSnapshot(string Path, bool ExistedBefore, byte[]? ContentBefore, string? ExpectedAfterHash);

    private sealed record ChangedFileSummary(string Path, int Added, int Removed);

    private sealed record RollbackCheckpoint(
        int Number,
        string Description,
        DateTimeOffset CreatedAt,
        IReadOnlyList<FileSnapshot> Files,
        IReadOnlyList<ChangedFileSummary> Changes);

    private sealed class ActiveTaskCheckpoint(string description)
    {
        public string Description { get; } = description;

        public Dictionary<string, FileSnapshot> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Record(IEnumerable<FileSnapshot> completed)
        {
            foreach (FileSnapshot snapshot in completed)
            {
                if (Files.TryGetValue(snapshot.Path, out FileSnapshot? existing))
                {
                    Files[snapshot.Path] = existing with { ExpectedAfterHash = snapshot.ExpectedAfterHash };
                }
                else
                {
                    Files.Add(snapshot.Path, snapshot);
                }
            }
        }
    }
}
