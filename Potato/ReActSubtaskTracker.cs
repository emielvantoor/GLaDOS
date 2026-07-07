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

        foreach (TrackedSubtask parsedSubtask in ExtractSubtasks(approach))
        {
            if (subtasks.Any(subtask => string.Equals(subtask.Name, parsedSubtask.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            subtasks.Add(parsedSubtask);
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
            return "approved execution steps";
        }

        return (subtasks.FirstOrDefault(subtask => subtask.Status == SubtaskStatus.InProgress) ??
                subtasks.FirstOrDefault(subtask => subtask.Status == SubtaskStatus.Pending) ??
                subtasks[^1]).Name;
    }

    public bool CurrentAllowsEditTools()
    {
        return CurrentAllowsTool(nameof(AgentTools.ApplySearchReplaceAsync)) ||
               CurrentAllowsTool(nameof(AgentTools.CreateFileAsync)) ||
               CurrentAllowsTool(nameof(AgentTools.ApplyDiffPatchAsync));
    }

    public bool CurrentAllowsTool(string toolName)
    {
        TrackedSubtask? current = CurrentSubtask();
        if (current is null)
        {
            return true;
        }

        if (current.AllowedTools.Count > 0)
        {
            return current.AllowedTools.Contains(toolName);
        }

        if (current.AllowsNoTool)
        {
            return false;
        }

        return !IsEditToolName(toolName) || LooksLikeWriteSubtask(current.Name.ToLowerInvariant());
    }

    public string CurrentToolRejectionReason(string toolName)
    {
        TrackedSubtask? current = CurrentSubtask();
        if (current is null)
        {
            return $"Rejected {toolName}: no current step/substep is active.";
        }

        string allowedTools = current.AllowsNoTool
            ? "no tools; this step must return DRAFT_RESULT"
            : current.AllowedTools.Count == 0
            ? "no explicit tool list was parsed for this step"
            : string.Join(", ", current.AllowedTools.OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase));

        return $"Rejected {toolName}: the current planned step/substep is '{current.Name}', and its approved Action allows {allowedTools}. " +
               "Use the approved tool for the current step, or emit READY_FOR_NEXT_SUBSTEP if the current step's stated Result is already satisfied.";
    }

    public string BuildPromptContext()
    {
        if (subtasks.Count == 0)
        {
            return "No structured steps or substeps were parsed from the execution steps. Continue against the approved execution steps.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("Tracked steps/substeps from the approved execution steps:");
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
            if (subtask.AllowedTools.Count > 0)
            {
                builder.Append("  Allowed tools from approved Action: ");
                builder.AppendLine(string.Join(", ", subtask.AllowedTools.OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase)));
            }
        }

        string currentName = CurrentDisplayName();
        builder.AppendLine("Current planned step/substep: ");
        builder.AppendLine(currentName);
        builder.Append("Current substep goal: ");
        builder.AppendLine(BuildSubstepGoal(currentName));
        builder.Append("Completion evidence for this substep: ");
        builder.AppendLine(BuildCompletionEvidence(currentName));
        builder.AppendLine("Do not write a full mini-plan. Use the goal and evidence to choose one tool call, READY_FOR_NEXT_SUBSTEP, or FINAL.");
        return builder.ToString();
    }

    public void MarkCurrentInProgress()
    {
        TrackedSubtask? current = CurrentSubtask();
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
        if (normalized.TrimStart().StartsWith("ready_for_next_substep:", StringComparison.Ordinal))
        {
            TrackedSubtask? current = CurrentSubtask();
            if (current is not null)
            {
                CompleteCurrentAndStartNext(current);
            }

            return;
        }

        if (normalized.TrimStart().StartsWith("draft_result:", StringComparison.Ordinal))
        {
            TrackedSubtask? current = CurrentSubtask();
            if (current is not null && current.AllowsNoTool)
            {
                CompleteCurrentAndStartNext(current);
            }

            return;
        }

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

        // Observations are evidence, not an implicit handoff. The model must compare
        // the latest observation with the approved step's stated Result and emit
        // READY_FOR_NEXT_SUBSTEP before the tracker advances.
        _ = normalizedName;
        _ = normalizedObservation;
        _ = normalizedSource;
    }

    public void MarkAllDone()
    {
        foreach (TrackedSubtask subtask in subtasks)
        {
            subtask.Status = SubtaskStatus.Done;
        }
    }

    public void Clear() => subtasks.Clear();

    private TrackedSubtask? CurrentSubtask()
    {
        if (subtasks.Count == 0)
        {
            return null;
        }

        return subtasks.FirstOrDefault(subtask => subtask.Status == SubtaskStatus.InProgress) ??
               subtasks.FirstOrDefault(subtask => subtask.Status == SubtaskStatus.Pending) ??
               subtasks[^1];
    }

    private static IEnumerable<TrackedSubtask> ExtractSubtasks(string approach)
    {
        bool inCodeFence = false;
        TrackedSubtask? current = null;
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

            if (IsPlanMetadataBoundary(line))
            {
                if (current is not null && ShouldTrackSubtask(current))
                {
                    yield return current;
                }

                current = null;
                continue;
            }

            Match match = SubtaskLineRegex().Match(line);
            if (match.Success)
            {
                if (current is not null && ShouldTrackSubtask(current))
                {
                    yield return current;
                }

                string name = CleanName(
                    match.Groups["named"].Success
                        ? match.Groups["named"].Value
                        : match.Groups["numbered"].Value);
                current = IsUsefulSubtaskName(name)
                    ? new TrackedSubtask(name)
                    : null;
                continue;
            }

            if (current is not null)
            {
                if (LooksLikeNoToolActionLine(line))
                {
                    current.AllowsNoTool = true;
                }

                foreach (string toolName in ExtractToolNames(line))
                {
                    current.AllowedTools.Add(toolName);
                }
            }
        }

        if (current is not null && ShouldTrackSubtask(current))
        {
            yield return current;
        }
    }

    private static bool ShouldTrackSubtask(TrackedSubtask subtask) =>
        IsUsefulSubtaskName(subtask.Name) &&
        (subtask.AllowedTools.Count > 0 || subtask.AllowsNoTool);

    private static string CleanName(string value)
    {
        string name = value.Trim().Trim('*', '`', '.', ':', '-', ' ');
        name = Regex.Replace(name, @"\s+", " ");
        name = Regex.Replace(name, @"^(?:subtask|step|task)\s+\d+\s*[:.-]\s*", string.Empty, RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"^\d+(?:\.\d+)*[.)]\s*", string.Empty, RegexOptions.IgnoreCase);
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
               !normalized.Equals("responsible for", StringComparison.Ordinal) &&
               !normalized.Equals("result", StringComparison.Ordinal) &&
               !normalized.Equals("tool", StringComparison.Ordinal) &&
               !normalized.Equals("tools used", StringComparison.Ordinal) &&
               !normalized.Contains("type 'execute'", StringComparison.Ordinal) &&
               !normalized.Contains("execution approval", StringComparison.Ordinal);
    }

    private static bool IsPlanMetadataBoundary(string line)
    {
        string normalized = line.Trim().Trim('-', '*', ' ', ':').ToLowerInvariant();
        return normalized.StartsWith("tools used", StringComparison.Ordinal) ||
               normalized.StartsWith("dependencies", StringComparison.Ordinal) ||
               normalized.StartsWith("execution approval", StringComparison.Ordinal) ||
               normalized.StartsWith("execution approval required", StringComparison.Ordinal);
    }

    private static bool LooksLikeNoToolActionLine(string line) =>
        line.Contains("no tool", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ExtractToolNames(string line)
    {
        foreach (Match match in ToolNameRegex().Matches(line))
        {
            string? toolName = CanonicalToolName(match.Groups["tool"].Value);
            if (toolName is not null)
            {
                yield return toolName;
            }
        }
    }

    private static string? CanonicalToolName(string toolName) =>
        toolName.ToLowerInvariant() switch
        {
            "getcurrenttime" => nameof(AgentTools.GetCurrentTime),
            "readfilecontent" => nameof(AgentTools.ReadFileContent),
            "listfiles" => nameof(AgentTools.ListFiles),
            "listprojectfiles" => nameof(AgentTools.ListProjectFiles),
            "searchfiles" => nameof(AgentTools.SearchFiles),
            "searchfilecontents" => nameof(AgentTools.SearchFileContents),
            "summarizefilepurpose" => nameof(AgentTools.SummarizeFilePurpose),
            "getcollectedcontext" => nameof(AgentTools.GetCollectedContext),
            "applysearchreplaceasync" => nameof(AgentTools.ApplySearchReplaceAsync),
            "createfileasync" => nameof(AgentTools.CreateFileAsync),
            "applydiffpatchasync" => nameof(AgentTools.ApplyDiffPatchAsync),
            "executeshellcommandasync" => nameof(AgentTools.ExecuteShellCommandAsync),
            _ => null
        };

    private static bool IsEditToolName(string toolName) =>
        toolName is nameof(AgentTools.ApplySearchReplaceAsync) or
            nameof(AgentTools.CreateFileAsync) or
            nameof(AgentTools.ApplyDiffPatchAsync);

    private static bool ContainsCompletionLanguage(string normalizedResponse, string subtaskName)
    {
        string normalizedName = subtaskName.ToLowerInvariant();
        return normalizedResponse.Contains($"{normalizedName} [done]", StringComparison.Ordinal) ||
               normalizedResponse.Contains($"{normalizedName}: done", StringComparison.Ordinal) ||
               normalizedResponse.Contains($"{normalizedName} complete", StringComparison.Ordinal) ||
               normalizedResponse.Contains($"completed {normalizedName}", StringComparison.Ordinal) ||
               normalizedResponse.Contains($"ready_for_next_substep: {normalizedName}", StringComparison.Ordinal);
    }

    private static string BuildSubstepGoal(string subtaskName)
    {
        string normalizedName = subtaskName.ToLowerInvariant();
        if (LooksLikeDuplicateSubtask(normalizedName))
        {
            return "identify duplicated content in the target document using actual repeated headings, phrases, or sections.";
        }

        if (LooksLikeContextSubtask(normalizedName))
        {
            return "collect only the context needed for this step/substep without editing files.";
        }

        if (LooksLikeEditSubtask(normalizedName))
        {
            return "apply the approved file change for this step/substep using the current file contents.";
        }

        if (LooksLikeVerificationSubtask(normalizedName))
        {
            return "verify the completed change or answer with focused evidence.";
        }

        return "complete the named step/substep while staying within the entire approved task.";
    }

    private static string BuildCompletionEvidence(string subtaskName)
    {
        string normalizedName = subtaskName.ToLowerInvariant();
        if (LooksLikeDuplicateSubtask(normalizedName))
        {
            return "the target document has been read or retrieved, and duplicate candidates are identified from actual repeated text or proven absent.";
        }

        if (LooksLikeContextSubtask(normalizedName))
        {
            return "relevant file listings, searches, reads, summaries, or retrieved collected context are available.";
        }

        if (LooksLikeEditSubtask(normalizedName))
        {
            return "the edit tool reports a successful write, create, or patch for the intended file.";
        }

        if (LooksLikeVerificationSubtask(normalizedName))
        {
            return "the verification command/result succeeds or the final read confirms the expected state.";
        }

        return "the latest observation proves this step/substep's requested work is done.";
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

    private static bool LooksLikeDuplicateSubtask(string normalizedName) =>
        normalizedName.Contains("duplicate", StringComparison.Ordinal) ||
        normalizedName.Contains("duplicated", StringComparison.Ordinal) ||
        normalizedName.Contains("redundan", StringComparison.Ordinal) ||
        normalizedName.Contains("repeat", StringComparison.Ordinal);

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

    [GeneratedRegex(@"^(?:[-*]\s+)?(?:\[[ xX]\]\s+)?(?:\*\*)?(?:(?:step|substep|task)\s+\d+(?:\.\d+)?\s*[:.-]\s*(?<named>[^*\r\n]{3,120})|(?<numbered>\d+(?:\.\d+)*[.)]\s+[^*\r\n]{3,120}))(?:\*\*)?\s*(?::|-\s+|\s*$)", RegexOptions.IgnoreCase)]
    private static partial Regex SubtaskLineRegex();

    [GeneratedRegex(@"`?(?<tool>GetCurrentTime|ReadFileContent|ListFiles|ListProjectFiles|SearchFiles|SearchFileContents|SummarizeFilePurpose|GetCollectedContext|ApplySearchReplaceAsync|CreateFileAsync|ApplyDiffPatchAsync|ExecuteShellCommandAsync)`?", RegexOptions.IgnoreCase)]
    private static partial Regex ToolNameRegex();

    private sealed class TrackedSubtask(string name)
    {
        public string Name { get; } = name;

        public HashSet<string> AllowedTools { get; } = new(StringComparer.Ordinal);

        public bool AllowsNoTool { get; set; }

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
