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
        yield return PlannerUser;
        yield return ArchitectRefactorSystem;
        yield return ArchitectRefactorUser;
        yield return DesignSystem;
        yield return DesignUser;
        yield return ApplyPatchSystem;
        yield return ApplyPatchUser;
        yield return WriteCodeSystem;
        yield return WriteCodeUser;
        yield return WriteDocumentationSystem;
        yield return WriteDocumentationUser;
        yield return CreateFileSystem;
        yield return CreateFileUser;
        yield return UserTextSystem;
        yield return UserTextUser;
        yield return CodeReviewSystem;
        yield return CodeReviewUser;
        yield return GreetingSystem;
        yield return SideQuestionSystem;
        yield return ProjectMapSystem;
        yield return ProjectMapUser;
        yield return ExecutionPlanningSystem;
        yield return ExecutionPlanningUser;
        yield return ShellScriptSystem;
        yield return ShellScriptUser;
        yield return ExecutionMemorySummaryUser;
        yield return FilePurposeUser;
        yield return GreetingUser;
        yield return ReActSystem;
        yield return ReActInitialUser;
        yield return ReActObservationUser;
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
