using System.Text.RegularExpressions;
using Potato.Tools;

namespace Potato.Session;

internal static class PlanningPathUtilities
{
    public static IReadOnlySet<string> ExtractIndexedPaths(string workspaceContext)
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

    public static IEnumerable<string> ExtractLikelyFileNames(string text)
    {
        foreach (Match match in Regex.Matches(
                     text,
                     @"(?<![\w./-])(?<file>[\w.-]+\.[A-Za-z0-9]+)(?![\w./-])"))
        {
            yield return match.Groups["file"].Value;
        }
    }

    public static bool TryExtractTargetFile(string argument, out string? targetFilePath)
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
        string path = CleanExtractedPath(pathEnd < 0 ? normalized[pathStart..] : normalized[pathStart..pathEnd]);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        targetFilePath = path;
        return true;
    }

    public static string ExtractDocumentationTargetPath(string argument)
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
        return CleanExtractedPath(pathEnd < 0 ? normalized[pathStart..] : normalized[pathStart..pathEnd]);
    }

    public static string CleanExtractedPath(string path)
    {
        string trimmed = path.Trim();
        int attachedMentionIndex = trimmed.IndexOf(" [@", StringComparison.Ordinal);
        if (attachedMentionIndex >= 0)
        {
            trimmed = trimmed[..attachedMentionIndex].TrimEnd();
        }

        int markdownLinkIndex = trimmed.IndexOf("](", StringComparison.Ordinal);
        if (markdownLinkIndex >= 0)
        {
            int linkStart = trimmed.LastIndexOf('[', markdownLinkIndex);
            if (linkStart > 0 && char.IsWhiteSpace(trimmed[linkStart - 1]))
            {
                trimmed = trimmed[..linkStart].TrimEnd();
            }
        }

        return trimmed;
    }

    public static string ReplaceExtractedTargetFile(string argument, string oldTargetFilePath, string replacement)
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

    public static bool TryResolveUniqueIndexedBasename(
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

    public static bool LooksLikeProjectPath(string value)
    {
        string path = NormalizeProjectPath(value);
        return path.Contains('/', StringComparison.Ordinal) || Path.HasExtension(path);
    }

    public static string NormalizeProjectPath(string path)
    {
        string normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized))
        {
            normalized = Path.GetRelativePath(PathResolver.WorkspaceRoot, normalized).Replace('\\', '/');
        }

        return normalized.TrimStart('/');
    }

    public static bool IsSkippedProjectMapPath(string filePath)
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
}
