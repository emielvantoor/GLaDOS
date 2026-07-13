using System.Text.RegularExpressions;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;

namespace Potato.Session;

public sealed class PlanTaskNormalizer
{
    public List<AgentTask> Normalize(
        IReadOnlyList<AgentTask> tasks,
        string goal,
        string workspaceContext,
        IReadOnlyList<TaskObservation> observations)
    {
        IReadOnlySet<string> indexedPaths = PlanningPathUtilities.ExtractIndexedPaths(workspaceContext);
        IReadOnlySet<string> availablePaths = AddObservedExistingPaths(indexedPaths, observations);
        tasks = PreferAttachedMentionPaths(tasks, goal, availablePaths);
        tasks = ResolveUniqueIndexedPathReferences(tasks, availablePaths);
        tasks = RewriteCreateFileForExistingDocumentation(tasks, availablePaths);
        return tasks.OrderBy(task => task.Step).ToList();
    }

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
            string path = PlanningPathUtilities.NormalizeProjectPath(match.Groups["path"].Value);
            if (PlanningPathUtilities.LooksLikeProjectPath(path))
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
            string path = PlanningPathUtilities.NormalizeProjectPath(match.Groups["path"].Value);
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
        if (PlanningPathUtilities.TryExtractTargetFile(argument, out string? targetFilePath) &&
            targetFilePath is not null &&
            TryGetAttachedReplacement(targetFilePath, attachedPathsByFileName, indexedPaths, out string? replacement))
        {
            return PlanningPathUtilities.ReplaceExtractedTargetFile(argument, targetFilePath, replacement);
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

        string createdPath = PlanningPathUtilities.NormalizeProjectPath(task.Argument);
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

            string laterPath = PlanningPathUtilities.NormalizeProjectPath(
                PlanningPathUtilities.ExtractDocumentationTargetPath(laterTask.Argument));
            if (string.Equals(laterPath, documentationPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
            PlanningPathUtilities.TryResolveUniqueIndexedBasename(argument, indexedPaths, out string resolvedReadPath))
        {
            return resolvedReadPath;
        }

        if ((normalizedAction == "apply-patch" ||
             normalizedAction == "write-code" ||
             normalizedAction == "write-documentation") &&
            PlanningPathUtilities.TryExtractTargetFile(argument, out string? targetFilePath) &&
            targetFilePath is not null &&
            PlanningPathUtilities.TryResolveUniqueIndexedBasename(targetFilePath, indexedPaths, out string resolvedTargetPath))
        {
            return PlanningPathUtilities.ReplaceExtractedTargetFile(argument, targetFilePath, resolvedTargetPath);
        }

        if (normalizedAction == "write-documentation" &&
            PlanningPathUtilities.TryResolveUniqueIndexedBasename(argument, indexedPaths, out string resolvedDocumentationPath))
        {
            return resolvedDocumentationPath;
        }

        return argument;
    }

    private static bool TryGetAttachedReplacement(
        string candidatePath,
        IReadOnlyDictionary<string, string> attachedPathsByFileName,
        IReadOnlySet<string> indexedPaths,
        out string replacement)
    {
        replacement = string.Empty;
        string normalizedCandidate = PlanningPathUtilities.NormalizeProjectPath(candidatePath);
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

    private static List<AgentTask> RenumberTasks(IEnumerable<AgentTask> tasks) =>
        tasks.Select((task, index) => task with { Step = index + 1 }).ToList();

    private sealed record AttachedMentionPath(string Path, string FileName);
}
