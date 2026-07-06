internal sealed class PromptFileStore
{
    private readonly string promptDirectory;
    private readonly Dictionary<string, string> cache = new(StringComparer.OrdinalIgnoreCase);

    public PromptFileStore(string promptDirectory)
    {
        this.promptDirectory = promptDirectory;
    }

    public string PromptDirectory => promptDirectory;

    public string LoadOrCreate(string fileName, string defaultText)
    {
        if (cache.TryGetValue(fileName, out string? cached))
        {
            return cached;
        }

        Directory.CreateDirectory(promptDirectory);
        string path = Path.Combine(promptDirectory, fileName);

        if (!File.Exists(path))
        {
            File.WriteAllText(path, defaultText);
            cache[fileName] = defaultText;
            return defaultText;
        }

        string text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            cache[fileName] = defaultText;
            return defaultText;
        }

        cache[fileName] = text;
        return text;
    }
}
