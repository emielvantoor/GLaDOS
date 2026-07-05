using Microsoft.Extensions.AI;

internal sealed class ReActMemory
{
    private const int SummaryThresholdCharacters = 3_000;
    private const int FullContentLimitCharacters = 12_000;

    private readonly List<ReActMemoryItem> items = [];

    public int Add(string source, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            content = "(empty)";
        }

        var item = new ReActMemoryItem(
            items.Count,
            source,
            BuildDescriptor(source, content),
            DateTimeOffset.Now,
            content);
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

        if (!int.TryParse(index, out int itemIndex))
        {
            return "Error: index must be 'list', 'latest', or a numeric collected context index.";
        }

        ReActMemoryItem? item = items.FirstOrDefault(candidate => candidate.Index == itemIndex);
        return item is null
            ? $"Error: No collected context item exists at index {itemIndex}."
            : FormatItem(item, full);
    }

    public async Task SummarizeLargeUnsummarizedItemsAsync(IChatClient summarizerClient)
    {
        foreach (ReActMemoryItem item in items.Where(item =>
                     item.Summary is null &&
                     item.Content.Length > SummaryThresholdCharacters))
        {
            item.Summary = await SummarizeAsync(summarizerClient, item);
            item.Descriptor = BuildDescriptor(item.Source, item.Summary);
        }
    }

    public void Clear() => items.Clear();

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
                return $"[{item.Index}] {item.Descriptor} | source: {item.Source} | {item.Content.Length} chars | preview: {Trim(preview, 140)}";
            }));
    }

    private static string FormatItem(ReActMemoryItem item, bool full)
    {
        string content = full
            ? Trim(item.Content, FullContentLimitCharacters)
            : item.Summary ?? Trim(item.Content, FullContentLimitCharacters);

        return $"[{item.Index}] {item.Descriptor}\nSource: {item.Source}\nCollected at: {item.CreatedAt:HH:mm:ss}\n{content}";
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

        if (trimmedSource.Equals("Assistant ReAct response", StringComparison.Ordinal))
        {
            return $"assistant response: {Trim(meaningfulLine, 100)}";
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

    private static async Task<string> SummarizeAsync(IChatClient summarizerClient, ReActMemoryItem item)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, PromptLibrary.SideQuestionSystemPrompt),
                new(
                    ChatRole.User,
                    "Summarize this collected ReAct context for later retrieval. " +
                    "Keep concrete file names, commands, errors, dependencies, and conclusions. " +
                    "Use concise bullets.\n\n" +
                    $"Source: {item.Source}\n\n" +
                    Trim(item.Content, FullContentLimitCharacters))
            };

            ChatResponse response = await summarizerClient.GetResponseAsync(messages, new ChatOptions());
            return string.IsNullOrWhiteSpace(response.Text)
                ? Trim(item.Content, SummaryThresholdCharacters)
                : response.Text.Trim();
        }
        catch
        {
            return Trim(item.Content, SummaryThresholdCharacters);
        }
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

    private sealed class ReActMemoryItem(
        int index,
        string source,
        string descriptor,
        DateTimeOffset createdAt,
        string content)
    {
        public int Index { get; } = index;

        public string Source { get; } = source;

        public string Descriptor { get; set; } = descriptor;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public string Content { get; } = content;

        public string? Summary { get; set; }
    }
}
