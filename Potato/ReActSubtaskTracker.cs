using System.Text;
using System.Text.RegularExpressions;

internal sealed partial class ReActSubtaskTracker
{
    private const int MaxSubtasks = 10;
    private readonly List<TrackedSubtask> subtasks = [];

    public bool HasSubtasks => subtasks.Count > 0;

    public void LoadFromApproach(string? approach)
    {
        subtasks.Clear();

        if (string.IsNullOrWhiteSpace(approach))
        {
            return;
        }

        foreach (string name in ExtractSubtaskNames(approach))
        {
            if (subtasks.Any(subtask => string.Equals(subtask.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            subtasks.Add(new TrackedSubtask(name));
            if (subtasks.Count >= MaxSubtasks)
            {
                break;
            }
        }
    }

    public string CurrentDisplayName()
    {
        if (subtasks.Count == 0)
        {
            return "approved approach";
        }

        return (subtasks.FirstOrDefault(subtask => subtask.Status == SubtaskStatus.InProgress) ??
                subtasks.FirstOrDefault(subtask => subtask.Status == SubtaskStatus.Pending) ??
                subtasks[^1]).Name;
    }

    public bool CurrentAllowsEditTools()
    {
        if (subtasks.Count == 0)
        {
            return true;
        }

        return LooksLikeWriteSubtask(CurrentDisplayName().ToLowerInvariant());
    }

    public string BuildPromptContext()
    {
        if (subtasks.Count == 0)
        {
            return "No structured subtasks were parsed from the approach. Continue against the approved approach.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("Tracked subtasks from the approved approach:");
        for (int i = 0; i < subtasks.Count; i++)
        {
            TrackedSubtask subtask = subtasks[i];
            builder.Append("- ");
            builder.Append(i + 1);
            builder.Append(". ");
            builder.Append(subtask.Name);
            builder.Append(" [");
            builder.Append(FormatStatus(subtask.Status));
            builder.AppendLine("]");
        }

        builder.Append("Current planned subtask: ");
        builder.Append(CurrentDisplayName());
        return builder.ToString();
    }

    public void MarkCurrentInProgress()
    {
        TrackedSubtask? current = subtasks.FirstOrDefault(subtask => subtask.Status == SubtaskStatus.InProgress) ??
                                  subtasks.FirstOrDefault(subtask => subtask.Status == SubtaskStatus.Pending);
        if (current is not null)
        {
            current.Status = SubtaskStatus.InProgress;
        }
    }

    public void UpdateFromAssistantResponse(string responseText)
    {
        if (subtasks.Count == 0 || string.IsNullOrWhiteSpace(responseText))
        {
            return;
        }

        string normalized = responseText.ToLowerInvariant();
        foreach (TrackedSubtask subtask in subtasks)
        {
            if (!normalized.Contains(subtask.Name.ToLowerInvariant(), StringComparison.Ordinal))
            {
                continue;
            }

            if (ContainsCompletionLanguage(normalized, subtask.Name))
            {
                subtask.Status = SubtaskStatus.Done;
            }
            else if (subtask.Status == SubtaskStatus.Pending)
            {
                subtask.Status = SubtaskStatus.InProgress;
            }

            break;
        }
    }

    public void UpdateFromObservation(string observationSource, string observation)
    {
        TrackedSubtask? current = subtasks.FirstOrDefault(subtask => subtask.Status == SubtaskStatus.InProgress);
        if (current is null || string.IsNullOrWhiteSpace(observation))
        {
            return;
        }

        current.ObservationCount++;
        string normalizedName = current.Name.ToLowerInvariant();
        string normalizedObservation = observation.ToLowerInvariant();
        string normalizedSource = observationSource.ToLowerInvariant();

        if (ObservationHasError(normalizedObservation))
        {
            return;
        }

        if (LooksLikeContextSubtask(normalizedName) &&
            (current.ObservationCount >= 2 ||
             normalizedSource.Contains("listfiles", StringComparison.Ordinal) ||
             normalizedSource.Contains("listprojectfiles", StringComparison.Ordinal) ||
             normalizedSource.Contains("readfilecontent", StringComparison.Ordinal) ||
             normalizedSource.Contains("searchfiles", StringComparison.Ordinal) ||
             normalizedSource.Contains("searchfilecontents", StringComparison.Ordinal) ||
             normalizedSource.Contains("summarizefilepurpose", StringComparison.Ordinal) ||
             normalizedObservation.Contains("source: listfiles", StringComparison.Ordinal) ||
             normalizedObservation.Contains("source: listprojectfiles", StringComparison.Ordinal) ||
             normalizedObservation.Contains("source: searchfiles", StringComparison.Ordinal) ||
             normalizedObservation.Contains("source: searchfilecontents", StringComparison.Ordinal) ||
             normalizedObservation.Contains("source: summarizefilepurpose", StringComparison.Ordinal) ||
             normalizedObservation.Contains("file content:", StringComparison.Ordinal)))
        {
            CompleteCurrentAndStartNext(current);
            return;
        }

        if (LooksLikeEditSubtask(normalizedName) &&
            (normalizedObservation.Contains("applied successfully", StringComparison.Ordinal) ||
             normalizedObservation.Contains("created successfully", StringComparison.Ordinal) ||
             normalizedObservation.Contains("patch applied successfully", StringComparison.Ordinal)))
        {
            CompleteCurrentAndStartNext(current);
            return;
        }

        if (LooksLikeVerificationSubtask(normalizedName) &&
            normalizedObservation.Contains("exit code: 0", StringComparison.Ordinal))
        {
            CompleteCurrentAndStartNext(current);
        }
    }

    public void MarkAllDone()
    {
        foreach (TrackedSubtask subtask in subtasks)
        {
            subtask.Status = SubtaskStatus.Done;
        }
    }

    public void Clear() => subtasks.Clear();

    private static IEnumerable<string> ExtractSubtaskNames(string approach)
    {
        bool inCodeFence = false;
        foreach (string rawLine in approach.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence || line.Length == 0)
            {
                continue;
            }

            Match match = SubtaskLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            string name = CleanName(match.Groups["name"].Value);
            if (IsUsefulSubtaskName(name))
            {
                yield return name;
            }
        }
    }

    private static string CleanName(string value)
    {
        string name = value.Trim().Trim('*', '`', '.', ':', '-', ' ');
        name = Regex.Replace(name, @"\s+", " ");
        name = Regex.Replace(name, @"^(?:subtask|step|task)\s+\d+\s*[:.-]\s*", string.Empty, RegexOptions.IgnoreCase);
        return name.Length <= 80 ? name : name[..80].Trim();
    }

    private static bool IsUsefulSubtaskName(string name)
    {
        if (name.Length < 3)
        {
            return false;
        }

        string normalized = name.ToLowerInvariant();
        return !normalized.Contains("available cli tools", StringComparison.Ordinal) &&
               !normalized.Equals("tools", StringComparison.Ordinal) &&
               !normalized.Equals("react loop", StringComparison.Ordinal) &&
               !normalized.Contains("type 'execute'", StringComparison.Ordinal) &&
               !normalized.Contains("execution approval", StringComparison.Ordinal);
    }

    private static bool ContainsCompletionLanguage(string normalizedResponse, string subtaskName)
    {
        string normalizedName = subtaskName.ToLowerInvariant();
        return normalizedResponse.Contains($"{normalizedName} [done]", StringComparison.Ordinal) ||
               normalizedResponse.Contains($"{normalizedName}: done", StringComparison.Ordinal) ||
               normalizedResponse.Contains($"{normalizedName} complete", StringComparison.Ordinal) ||
               normalizedResponse.Contains($"completed {normalizedName}", StringComparison.Ordinal);
    }

    private void CompleteCurrentAndStartNext(TrackedSubtask current)
    {
        current.Status = SubtaskStatus.Done;
        TrackedSubtask? next = subtasks.FirstOrDefault(subtask => subtask.Status == SubtaskStatus.Pending);
        if (next is not null)
        {
            next.Status = SubtaskStatus.InProgress;
        }
    }

    private static bool ObservationHasError(string normalizedObservation) =>
        normalizedObservation.Contains("error:", StringComparison.Ordinal) ||
        normalizedObservation.Contains("failed", StringComparison.Ordinal) ||
        normalizedObservation.Contains("rejected", StringComparison.Ordinal) ||
        normalizedObservation.Contains("denied", StringComparison.Ordinal) ||
        normalizedObservation.Contains("timed out", StringComparison.Ordinal) ||
        normalizedObservation.Contains("exit code: 1", StringComparison.Ordinal);

    private static bool LooksLikeContextSubtask(string normalizedName) =>
        normalizedName.Contains("context", StringComparison.Ordinal) ||
        normalizedName.Contains("discover", StringComparison.Ordinal) ||
        normalizedName.Contains("inspect", StringComparison.Ordinal) ||
        normalizedName.Contains("inventory", StringComparison.Ordinal) ||
        normalizedName.Contains("list", StringComparison.Ordinal) ||
        normalizedName.Contains("read", StringComparison.Ordinal) ||
        normalizedName.Contains("summar", StringComparison.Ordinal) ||
        normalizedName.Contains("understand", StringComparison.Ordinal);

    private static bool LooksLikeEditSubtask(string normalizedName) =>
        normalizedName.Contains("implement", StringComparison.Ordinal) ||
        normalizedName.Contains("edit", StringComparison.Ordinal) ||
        normalizedName.Contains("update", StringComparison.Ordinal) ||
        normalizedName.Contains("change", StringComparison.Ordinal) ||
        normalizedName.Contains("document", StringComparison.Ordinal) ||
        normalizedName.Contains("patch", StringComparison.Ordinal);

    private static bool LooksLikeWriteSubtask(string normalizedName)
    {
        if (normalizedName.Contains("preparation", StringComparison.Ordinal) ||
            normalizedName.Contains("prepare", StringComparison.Ordinal) ||
            normalizedName.Contains("planning", StringComparison.Ordinal) ||
            normalizedName.Contains("compare", StringComparison.Ordinal) ||
            normalizedName.Contains("summar", StringComparison.Ordinal) ||
            LooksLikeContextSubtask(normalizedName) ||
            LooksLikeVerificationSubtask(normalizedName))
        {
            return false;
        }

        return normalizedName.Contains("modification", StringComparison.Ordinal) ||
               normalizedName.Contains("modify", StringComparison.Ordinal) ||
               normalizedName.Contains("write", StringComparison.Ordinal) ||
               normalizedName.Contains("append", StringComparison.Ordinal) ||
               normalizedName.Contains("insert", StringComparison.Ordinal) ||
               normalizedName.Contains("create", StringComparison.Ordinal) ||
               normalizedName.Contains("apply", StringComparison.Ordinal) ||
               normalizedName.Contains("patch", StringComparison.Ordinal) ||
               normalizedName.Contains("implementation", StringComparison.Ordinal) ||
               normalizedName.Contains("implement", StringComparison.Ordinal) ||
               normalizedName.Contains("edit", StringComparison.Ordinal);
    }

    private static bool LooksLikeVerificationSubtask(string normalizedName) =>
        normalizedName.Contains("verify", StringComparison.Ordinal) ||
        normalizedName.Contains("test", StringComparison.Ordinal) ||
        normalizedName.Contains("build", StringComparison.Ordinal) ||
        normalizedName.Contains("validate", StringComparison.Ordinal);

    private static string FormatStatus(SubtaskStatus status) =>
        status switch
        {
            SubtaskStatus.Pending => "pending",
            SubtaskStatus.InProgress => "in progress",
            SubtaskStatus.Done => "done",
            _ => "unknown"
        };

    [GeneratedRegex(@"^(?:[-*]\s+|\d+[.)]\s+)(?:\[[ xX]\]\s+)?(?:\*\*)?(?<name>[^*\r\n]{3,120})(?:\*\*)?\s*(?::|-\s+|\s*$)")]
    private static partial Regex SubtaskLineRegex();

    private sealed class TrackedSubtask(string name)
    {
        public string Name { get; } = name;

        public SubtaskStatus Status { get; set; } = SubtaskStatus.Pending;

        public int ObservationCount { get; set; }
    }

    private enum SubtaskStatus
    {
        Pending,
        InProgress,
        Done
    }
}
