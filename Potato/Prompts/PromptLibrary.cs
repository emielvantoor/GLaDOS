namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static PromptFileStore promptFileStore = new(Path.Combine(AppContext.BaseDirectory, "prompts"));
    private static bool useCompiledDefaultsOnly;

    public static bool UseCompiledDefaultsOnly => useCompiledDefaultsOnly;

    public static void Configure(string promptDirectory, bool useCompiledDefaultsOnly)
    {
        promptFileStore = new PromptFileStore(promptDirectory);
        PromptLibrary.useCompiledDefaultsOnly = useCompiledDefaultsOnly;
        BootstrapPromptFiles();
    }

    public static void SetUseCompiledDefaultsOnly(bool value)
    {
        useCompiledDefaultsOnly = value;
        if (!value)
        {
            BootstrapPromptFiles();
        }
    }

    private static void BootstrapPromptFiles()
    {
        foreach (PromptDefinition promptDefinition in GetPromptDefinitions())
        {
            _ = Load(promptDefinition);
        }
    }

    private static IEnumerable<PromptDefinition> GetPromptDefinitions()
    {
        yield return GreetingSystem;
        yield return SideQuestionSystem;
        yield return ProjectMapSystem;
        yield return ProjectMapUser;
        yield return ExecutionMemorySummaryUser;
        yield return FilePurposeUser;
        yield return GreetingUser;
        yield return ReActSystem;
        yield return ReActInitialUser;
        yield return ReActObservationUser;
        yield return DirectExecutionGuidance;
    }

    private static string Load(PromptDefinition definition) =>
        useCompiledDefaultsOnly
            ? definition.DefaultText
            : promptFileStore.LoadOrCreate(definition.FileName, definition.DefaultText);

    private static string Render(PromptDefinition definition, IReadOnlyDictionary<string, string> values)
    {
        string text = Load(definition);
        foreach ((string key, string value) in values)
        {
            text = text.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
        }

        return text;
    }

    private sealed record PromptDefinition(string FileName, string DefaultText);
}
