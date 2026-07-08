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
        yield return PlannerSystem;
        yield return PatchSystem;
        yield return RefactorSystem;
        yield return CreateFileSystem;
        yield return UserTextSystem;
        yield return CodeReviewSystem;
        yield return GreetingSystem;
        yield return SideQuestionSystem;
    }

    private static string Load(PromptDefinition definition) =>
        useCompiledDefaultsOnly
            ? definition.DefaultText
            : promptFileStore.LoadOrCreate(definition.FileName, definition.DefaultText);

    private sealed record PromptDefinition(string FileName, string DefaultText);
}