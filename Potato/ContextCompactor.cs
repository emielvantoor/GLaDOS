using System.Text;
using Potato.Models;

namespace Potato;

/// <summary>
/// Intelligent truncation of tool results based on result type.
/// Different result types are truncated using context-aware strategies to preserve the most valuable information.
/// Returns truncated content + metadata (context key, retrieval hint) for chat history.
/// Full content is stored in ExecutionMemory for later retrieval via GetCollectedContext.
/// </summary>
internal sealed class ContextCompactor
{
    private static int _contextKeyCounter = 0;
    private readonly object _keyLock = new();

    /// <summary>
    /// Result from compacting a tool result for chat history inclusion.
    /// </summary>
    public sealed record CompactionResult(
        string TruncatedContent,      // What goes in chat history
        string ContextKey,            // e.g., "ref#42" for retrieval
        bool WasTruncated,           // Whether original was longer than truncated
        int? OriginalLength,         // Full content length if truncated
        string RetrievalHint         // Human-readable hint about what was trimmed
    );

    /// <summary>
    /// Intelligently truncates tool results based on type.
    /// Returns compact content for chat history + metadata for tracking.
    /// For code files, minifies content and extracts public API (no truncation).
    /// </summary>
    public CompactionResult Compact(
        string content,
        ToolResultType resultType,
        int maxCharacters = 0,
        string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new CompactionResult(
                TruncatedContent: "(empty)",
                ContextKey: GenerateContextKey(),
                WasTruncated: false,
                OriginalLength: 0,
                RetrievalHint: "Empty result");
        }

        string trimmed = content.Trim();
        if (maxCharacters <= 0)
        {
            maxCharacters = GetDefaultMaxCharacters(resultType);
        }

        // Special handling for code files: minify without truncation
        if (resultType == ToolResultType.FileContent && !string.IsNullOrEmpty(filePath) && CodeMinifier.IsCodeFile(filePath))
        {
            var (minified, hint) = MinifyCodeFile(trimmed, filePath!, maxCharacters);
            return new CompactionResult(
                // TruncatedContent: $"{minified}\n[MINIFIED • ref#{{{{INDEX}}}} • use GetCollectedContext(\"{{{{INDEX}}}}\", full=true)]",
                TruncatedContent: minified,
                ContextKey: GenerateContextKey(),
                WasTruncated: false,  // Always return full minified content, never truncate
                OriginalLength: trimmed.Length,
                RetrievalHint: hint);
        }

        // If content fits, no truncation needed
        if (trimmed.Length <= maxCharacters)
        {
            return new CompactionResult(
                TruncatedContent: trimmed,
                ContextKey: GenerateContextKey(),
                WasTruncated: false,
                OriginalLength: trimmed.Length,
                RetrievalHint: null);
        }

        // Content is too large; use type-specific strategy
        var (truncated, hint2) = resultType switch
        {
            ToolResultType.FileContent => TruncateFileContent(trimmed, maxCharacters, filePath),
            ToolResultType.ShellOutput => TruncateShellOutput(trimmed, maxCharacters),
            ToolResultType.SearchResults => TruncateSearchResults(trimmed, maxCharacters),
            ToolResultType.DirectoryListing => TruncateDirectoryListing(trimmed, maxCharacters),
            ToolResultType.PatchDiff => TruncatePatchDiff(trimmed, maxCharacters),
            ToolResultType.Error => (trimmed, "Error: kept full for debugging"),
            ToolResultType.Summary or ToolResultType.SystemInfo => (trimmed, "Summary/system info kept full"),
            _ => TruncateGeneric(trimmed, maxCharacters)
        };

        string contextKey = GenerateContextKey();
        return new CompactionResult(
            TruncatedContent: $"{truncated}\n[TRUNCATED • ref#{{{{INDEX}}}} • use GetCollectedContext(\"{{{{INDEX}}}}\", full=true)]",
            ContextKey: contextKey,
            WasTruncated: true,
            OriginalLength: trimmed.Length,
            RetrievalHint: hint2);
    }

    /// <summary>
    /// File content: keep first N lines + last M lines to show structure while avoiding middle bloat.
    /// Only called for non-code files or code files that weren't minified.
    /// </summary>
    private static (string, string) TruncateFileContent(string content, int maxChars, string? filePath)
    {
        // If we have a file path and it's code, minify and show API
        if (!string.IsNullOrEmpty(filePath) && CodeMinifier.IsCodeFile(filePath))
        {
            return MinifyCodeFile(content, filePath, maxChars);
        }

        // Default: keep first N lines + last M lines
        string[] lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        const int linesToKeepPerSide = 15;
        int maxLinesPerSide = Math.Max(5, maxChars / 80);
        int keepPerSide = Math.Min(linesToKeepPerSide, maxLinesPerSide);

        if (lines.Length <= keepPerSide * 2)
        {
            return (content, $"File has {lines.Length} lines");
        }

        var result = new StringBuilder();
        result.AppendLine("--- [First " + keepPerSide + " lines] ---");
        result.AppendLine(string.Join("\n", lines[..keepPerSide]));
        result.AppendLine($"\n... ({lines.Length - (keepPerSide * 2)} lines hidden) ...\n");
        result.AppendLine("--- [Last " + keepPerSide + " lines] ---");
        result.AppendLine(string.Join("\n", lines[^keepPerSide..]));

        return (result.ToString().TrimEnd(),
                $"File: {lines.Length} lines total, showing edges");
    }

    /// <summary>
    /// Minify code file and extract public API summary.
    /// Returns full minified content with API header (no truncation).
    /// </summary>
    private static (string, string) MinifyCodeFile(string content, string filePath, int maxChars)
    {
        // Minify the code
        string minified = CodeMinifier.Minify(content, filePath);
        
        // Extract public API summary
        string apiSummary = CodeAnalyzer.ExtractPublicApi(minified, filePath);
        
        // Build result: API summary first, then minified code
        var result = new StringBuilder();
        
        if (!string.IsNullOrEmpty(apiSummary))
        {
            result.AppendLine(apiSummary);
            result.AppendLine();
        }
        
        result.Append(minified);
        
        string finalContent = result.ToString().TrimEnd();
        int originalLines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).Length;
        int minifiedLines = finalContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).Length;
        
        // For code files: always return full minified content (no truncation)
        // Mark as not truncated since the LM gets the complete minified code
        return (finalContent,
                $"Code file: {originalLines} → {minifiedLines} lines after minification");
    }

    /// <summary>
    /// Shell output: keep first lines (status), last lines (results/errors), and any error sections.
    /// </summary>
    private static (string, string) TruncateShellOutput(string content, int maxChars)
    {
        string[] lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        const int linesToKeepPerSide = 10;
        int maxLinesPerSide = Math.Max(3, maxChars / 80);
        int keepPerSide = Math.Min(linesToKeepPerSide, maxLinesPerSide);

        var result = new StringBuilder();
        result.AppendLine("--- [First " + keepPerSide + " lines] ---");
        result.AppendLine(string.Join("\n", lines[..Math.Min(keepPerSide, lines.Length)]));

        // Look for error patterns in middle
        int errorLineIndex = Array.FindIndex(lines, l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || 
                                                        l.Contains("exception", StringComparison.OrdinalIgnoreCase));
        
        if (errorLineIndex > keepPerSide && errorLineIndex < lines.Length - keepPerSide)
        {
            int startErr = Math.Max(keepPerSide, errorLineIndex - 2);
            int endErr = Math.Min(lines.Length, errorLineIndex + 3);
            result.AppendLine($"\n--- [Error context around line {errorLineIndex}] ---");
            result.AppendLine(string.Join("\n", lines[startErr..endErr]));
        }

        if (lines.Length > keepPerSide)
        {
            result.AppendLine($"\n... ({lines.Length - (keepPerSide * 2)} lines hidden) ...\n");
            result.AppendLine("--- [Last " + keepPerSide + " lines] ---");
            result.AppendLine(string.Join("\n", lines[^keepPerSide..]));
        }

        return (result.ToString().TrimEnd(),
                $"Shell output: {lines.Length} lines total, showing structure");
    }

    /// <summary>
    /// Search results: keep top N results + show count of total matches.
    /// </summary>
    private static (string, string) TruncateSearchResults(string content, int maxChars)
    {
        string[] lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        int linesToKeep = Math.Max(5, Math.Min(20, maxChars / 100));
        
        if (lines.Length <= linesToKeep + 2)
        {
            return (content, $"Search: {lines.Length} results");
        }

        var result = new StringBuilder();
        result.AppendLine(string.Join("\n", lines[..linesToKeep]));
        result.AppendLine($"\n... and {lines.Length - linesToKeep} more results ...");

        return (result.ToString().TrimEnd(),
                $"Search: {lines.Length} results total, showing top {linesToKeep}");
    }

    /// <summary>
    /// Directory listing: keep first N entries + count.
    /// </summary>
    private static (string, string) TruncateDirectoryListing(string content, int maxChars)
    {
        string[] lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        int linesToKeep = Math.Max(8, Math.Min(25, maxChars / 100));
        
        if (lines.Length <= linesToKeep + 2)
        {
            return (content, $"Directory: {lines.Length} items");
        }

        var result = new StringBuilder();
        result.AppendLine(string.Join("\n", lines[..linesToKeep]));
        result.AppendLine($"\n... ({lines.Length - linesToKeep} more items) ...");

        return (result.ToString().TrimEnd(),
                $"Directory: {lines.Length} items total, showing first {linesToKeep}");
    }

    /// <summary>
    /// Patch/diff: keep change hunks and context while trimming large sections.
    /// </summary>
    private static (string, string) TruncatePatchDiff(string content, int maxChars)
    {
        // Simple heuristic: keep @@ lines (hunk headers) and surrounding context
        string[] lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        var result = new StringBuilder();
        int currentHunks = 0;
        int keptLines = 0;

        foreach (string line in lines)
        {
            if (line.StartsWith("@@"))
            {
                currentHunks++;
                if (keptLines > maxChars * 0.8)
                    break;
                result.AppendLine(line);
                keptLines += line.Length;
            }
            else if (currentHunks > 0 && keptLines < maxChars * 0.9)
            {
                result.AppendLine(line);
                keptLines += line.Length;
            }
        }

        return (result.ToString().TrimEnd(),
                $"Patch: {currentHunks} hunks, showing structure");
    }

    /// <summary>
    /// Generic truncation: simple character cutoff with ellipsis.
    /// </summary>
    private static (string, string) TruncateGeneric(string content, int maxChars)
    {
        return (content[..maxChars],
                $"Truncated: kept first {maxChars:N0} characters");
    }

    /// <summary>
    /// Get default max characters based on result type.
    /// Balance between preserving information and saving tokens.
    /// </summary>
    private static int GetDefaultMaxCharacters(ToolResultType resultType) =>
        resultType switch
        {
            ToolResultType.FileContent => 6_000,       // Keep enough code to see structure
            ToolResultType.ShellOutput => 4_000,       // Shell output can be verbose
            ToolResultType.SearchResults => 3_000,     // Search results are usually items
            ToolResultType.DirectoryListing => 2_000,  // Listings are repetitive
            ToolResultType.PatchDiff => 5_000,         // Patches can be large
            ToolResultType.Error => int.MaxValue,      // Never truncate errors
            ToolResultType.Summary => int.MaxValue,    // Summaries should be complete
            ToolResultType.SystemInfo => int.MaxValue, // System info is small
            _ => 12_000                                 // Generic fallback
        };

    /// <summary>
    /// Generate a unique context key for tracking truncated results.
    /// Returns just the numeric ID (e.g., "42"), which gets prefixed as "ref#42" in messages.
    /// </summary>
    private string GenerateContextKey()
    {
        lock (_keyLock)
        {
            return Interlocked.Increment(ref _contextKeyCounter).ToString();
        }
    }
}
