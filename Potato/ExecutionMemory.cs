using Microsoft.Extensions.AI;
using Potato.Models;

namespace Potato;

public sealed class ExecutionMemory
{
    private const int SummaryThresholdCharacters = 3_000;
    private const int FullContentLimitCharacters = 12_000;

    private readonly List<ExecutionMemoryItem> items = [];

    public int Count => items.Count;

    public int Add(string source, string content)
    {
        return Add(source, content, ToolResultType.Generic);
    }

    public int Add(string source, string content, ToolResultType resultType, int? truncatedLength = null, string? retrievalHint = null, string? contextKey = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            content = "(empty)";
        }

        var item = new ExecutionMemoryItem(
            items.Count,
            source,
            BuildDescriptor(source, content),
            DateTimeOffset.Now,
            content,
            resultType,
            contextKey,
            truncatedLength,
            retrievalHint);
        items.Add(item);
        return item.Index;
    }

    public string Get(string index, bool full = false)
    {
        if (string.IsNullOrWhiteSpace(index) ||
            index.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return List();
        }

        if (index.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return items.Count == 0 ? "No collected context is available." : FormatItem(items[^1], full);
        }

        // Handle both "3" and "ref#3" formats
        string normalizedIndex = index.StartsWith("ref#", StringComparison.OrdinalIgnoreCase)
            ? index.Substring(4)
            : index;

        if (!int.TryParse(normalizedIndex, out int itemIndex))
        {
            return $"Error: index must be 'list', 'latest', a numeric index (e.g., '5'), or reference key (e.g., 'ref#5'). You provided: '{index}'";
        }

        ExecutionMemoryItem? item = items.FirstOrDefault(candidate => candidate.Index == itemIndex);
        return item is null
            ? $"Error: No collected context item exists at index {itemIndex}."
            : FormatItem(item, full);
    }

    public string GetRange(int startIndex, int endIndex, bool full = false)
    {
        ExecutionMemoryItem[] range = items
            .Where(item => item.Index >= startIndex && item.Index < endIndex)
            .ToArray();

        if (range.Length == 0)
        {
            return "No new collected context is available.";
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            range.Select(item => FormatItem(item, full)));
    }

    public async Task SummarizeLargeUnsummarizedItemsAsync(
        string? goal,
        IChatClient summarizerClient,
        CancellationToken cancellationToken = default)
    {
        foreach (ExecutionMemoryItem item in items.Where(item =>
                     item.Summary is null &&
                     item.Content.Length > SummaryThresholdCharacters))
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.Summary = await SummarizeAsync(summarizerClient, item, goal, cancellationToken);
            item.Descriptor = BuildDescriptor(item.Source, item.Summary);
        }
    }

    /// <summary>
    /// Gets the summary confidence level from a summary (looks for [Confidence: X] marker)
    /// </summary>
    public ConfidenceLevel GetSummaryConfidence(int itemIndex)
    {
        ExecutionMemoryItem? item = items.FirstOrDefault(candidate => candidate.Index == itemIndex);
        if (item?.Summary is null)
        {
            return ConfidenceLevel.Unknown;
        }

        return ParseConfidenceLevel(item.Summary);
    }

    /// <summary>
    /// Check if summary has high confidence (can edit with just summary, no need for full content)
    /// </summary>
    public bool IsSummaryHighConfidence(int itemIndex)
    {
        return GetSummaryConfidence(itemIndex) == ConfidenceLevel.High;
    }

    public void Clear() => items.Clear();

    public (int TotalCharacters, int TruncatedCount, int TotalBytesRecovered) GetMetrics()
    {
        int totalChars = 0;
        int truncatedCount = 0;
        int bytesRecovered = 0;

        foreach (var item in items)
        {
            totalChars += item.Content.Length;
            if (item.TruncatedLength.HasValue && item.TruncatedLength > item.Content.Length)
            {
                truncatedCount++;
                bytesRecovered += item.TruncatedLength.Value - item.Content.Length;
            }
        }

        return (totalChars, truncatedCount, bytesRecovered);
    }

    private string List()
    {
        if (items.Count == 0)
        {
            return "No collected context is available.";
        }

        return string.Join(
            Environment.NewLine,
            items.Select(item =>
            {
                string preview = item.Summary ?? FirstLine(item.Content);
                string typeInfo = item.ResultType != ToolResultType.Generic 
                    ? $" | type: {item.ResultType}" 
                    : string.Empty;
                string refInfo = !string.IsNullOrEmpty(item.ContextKey)
                    ? $" | {item.ContextKey}"
                    : string.Empty;
                string truncInfo = item.TruncatedLength.HasValue
                    ? $" | [TRUNCATED]"
                    : string.Empty;
                return $"[{item.Index}] {item.Descriptor} | source: {item.Source}{typeInfo}{refInfo}{truncInfo} | {item.Content.Length} chars | preview: {Trim(preview, 140)}";
            }));
    }

    private static string FormatItem(ExecutionMemoryItem item, bool full)
    {
        string content = full
            ? Trim(item.Content, FullContentLimitCharacters)
            : item.Summary ?? Trim(item.Content, FullContentLimitCharacters);

        var descriptor = new System.Text.StringBuilder();
        descriptor.Append($"[{item.Index}] {item.Descriptor}\n");
        descriptor.Append($"Source: {item.Source}\n");
        descriptor.Append($"Collected at: {item.CreatedAt:HH:mm:ss}\n");
        
        if (item.ResultType != ToolResultType.Generic)
        {
            descriptor.Append($"Type: {item.ResultType}\n");
        }

        if (!string.IsNullOrEmpty(item.ContextKey))
        {
            descriptor.Append($"Reference: {item.ContextKey}\n");
        }

        if (item.TruncatedLength.HasValue && item.TruncatedLength > item.Content.Length)
        {
            descriptor.Append($"[Truncated: original {item.TruncatedLength:N0} chars → {item.Content.Length:N0} chars shown]\n");
        }

        if (!string.IsNullOrEmpty(item.RetrievalHint))
        {
            descriptor.Append($"Hint: {item.RetrievalHint}\n");
        }

        descriptor.Append(content);
        return descriptor.ToString();
    }

    private static string BuildDescriptor(string source, string content)
    {
        string trimmedSource = Trim(source, 120).Replace('\n', ' ');
        string meaningfulLine = FirstMeaningfulLine(content);

        if (trimmedSource.Contains("ReadFileContent ", StringComparison.Ordinal))
        {
            return $"file content: {trimmedSource["ReadFileContent ".Length..]}";
        }

        if (trimmedSource.Contains("ExecuteShellCommandAsync ", StringComparison.Ordinal))
        {
            return $"shell result: {trimmedSource["ExecuteShellCommandAsync ".Length..]}";
        }

        if (trimmedSource.Equals("GetCurrentTime", StringComparison.Ordinal))
        {
            return "current time result";
        }

        if (trimmedSource.Equals("ApplyDiffPatchAsync", StringComparison.Ordinal))
        {
            return $"patch result: {Trim(meaningfulLine, 100)}";
        }

        return $"{trimmedSource}: {Trim(meaningfulLine, 100)}";
    }

    private static async Task<string> SummarizeAsync(
        IChatClient summarizerClient,
        ExecutionMemoryItem item,
        string? goal,
        CancellationToken cancellationToken)
    {
        try
        {
            // Use goal-aware prompt if goal provided, otherwise generic
            string userPrompt = string.IsNullOrWhiteSpace(goal)
                ? Potato.Prompts.PromptLibrary.BuildExecutionMemorySummaryUserPrompt(
                    item.Source,
                    Trim(item.Content, FullContentLimitCharacters))
                : Potato.Prompts.PromptLibrary.BuildExecutionMemorySummaryGoalAwarePrompt(
                    goal,
                    item.Source,
                    Trim(item.Content, FullContentLimitCharacters));

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, Potato.Prompts.PromptLibrary.SideQuestionSystemPrompt),
                new(ChatRole.User, userPrompt)
            };

            ChatResponse response = await summarizerClient.GetResponseAsync(messages, new ChatOptions(), cancellationToken);
            return string.IsNullOrWhiteSpace(response.Text)
                ? Trim(item.Content, SummaryThresholdCharacters)
                : response.Text.Trim();
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return Trim(item.Content, SummaryThresholdCharacters);
        }
    }

    private static ConfidenceLevel ParseConfidenceLevel(string summary)
    {
        // Look for [Confidence: X] marker anywhere in summary
        int idx = summary.IndexOf("[Confidence:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return ConfidenceLevel.Unknown;
        }

        string after = summary[idx..];
        if (after.Contains("HIGH", StringComparison.OrdinalIgnoreCase))
        {
            return ConfidenceLevel.High;
        }
        if (after.Contains("MEDIUM", StringComparison.OrdinalIgnoreCase))
        {
            return ConfidenceLevel.Medium;
        }
        if (after.Contains("LOW", StringComparison.OrdinalIgnoreCase))
        {
            return ConfidenceLevel.Low;
        }

        return ConfidenceLevel.Unknown;
    }

    private static string FirstLine(string text)
    {
        string normalized = text.Trim();
        int lineEnd = normalized.IndexOf('\n');
        return lineEnd < 0 ? normalized : normalized[..lineEnd];
    }

    private static string FirstMeaningfulLine(string text)
    {
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                trimmed.Equals("Stdout:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("Stderr:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Exit code:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return trimmed;
        }

        return FirstLine(text);
    }

    private static string Trim(string text, int maxCharacters)
    {
        string normalized = text.Trim();
        return normalized.Length <= maxCharacters
            ? normalized
            : normalized[..maxCharacters] + "\n...(truncated)";
    }

    private sealed class ExecutionMemoryItem(
        int index,
        string source,
        string descriptor,
        DateTimeOffset createdAt,
        string content,
        ToolResultType resultType = ToolResultType.Generic,
        string? contextKey = null,
        int? truncatedLength = null,
        string? retrievalHint = null)
    {
        public int Index { get; } = index;

        public string Source { get; } = source;

        public string Descriptor { get; set; } = descriptor;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public string Content { get; } = content;

        public string? Summary { get; set; }

        public ToolResultType ResultType { get; } = resultType;

        public string? ContextKey { get; } = contextKey;

        public int? TruncatedLength { get; } = truncatedLength;

        public string? RetrievalHint { get; } = retrievalHint;
    }
}
