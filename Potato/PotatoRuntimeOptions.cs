namespace Potato;

public sealed class PotatoRuntimeOptions
{
    private const int DefaultContextSize = 32768;

    public string PromptDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "prompts");

    public bool UseCompiledDefaultPrompts { get; init; }

    public bool WebUiInputEnabled { get; init; }

    public int ContextSize { get; init; } = DefaultContextSize;

    public bool AcpMode { get; init; }

    public string? Model { get; init; }

    public HashSet<string> AlwaysAllowedPermissionKeys { get; } = new(StringComparer.Ordinal);

    public static PotatoRuntimeOptions FromArgs(string[] args, PotatoAppSettings appSettings)
    {
        return new PotatoRuntimeOptions
        {
            PromptDirectory = GetPromptDirectory(args),
            UseCompiledDefaultPrompts = appSettings.UseCompiledDefaultPrompts,
            WebUiInputEnabled = GetWebUiInputEnabled(appSettings),
            ContextSize = GetContextSize(appSettings),
            AcpMode = args.Any(arg => arg.Equals("--acp", StringComparison.OrdinalIgnoreCase)),
            Model = GetModel(args, appSettings.SelectedModel)
        };
    }

    private static int GetContextSize(PotatoAppSettings appSettings)
    {
        string? environmentValue = Environment.GetEnvironmentVariable("POTATO_CONTEXT_SIZE");
        if (!string.IsNullOrWhiteSpace(environmentValue) &&
            int.TryParse(environmentValue, out int environmentContextSize) &&
            environmentContextSize > 0)
        {
            return environmentContextSize;
        }

        return appSettings.ContextSize is > 0 ? appSettings.ContextSize.Value : DefaultContextSize;
    }

    private static bool GetWebUiInputEnabled(PotatoAppSettings appSettings)
    {
        string? environmentValue = Environment.GetEnvironmentVariable("POTATO_WEBUI_ALLOW_INPUT");
        if (string.IsNullOrWhiteSpace(environmentValue))
        {
            return appSettings.WebUiInputEnabled;
        }

        return string.Equals(environmentValue, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(environmentValue, "1", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPromptDirectory(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--prompts", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return Path.GetFullPath(args[i + 1]);
            }

            const string prefix = "--prompts=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(arg[prefix.Length..]);
            }
        }

        string? environmentPath = Environment.GetEnvironmentVariable("POTATO_PROMPTS_DIR");
        return string.IsNullOrWhiteSpace(environmentPath)
            ? Path.Combine(AppContext.BaseDirectory, "prompts")
            : Path.GetFullPath(environmentPath);
    }

    private static string? GetModel(string[] args, string? savedModel)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--model", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1].Trim();
            }

            const string prefix = "--model=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[i][prefix.Length..].Trim();
            }
        }

        return string.IsNullOrWhiteSpace(savedModel) ? null : savedModel;
    }

}

internal enum ToolPermissionChoice
{
    Deny,
    AllowOnce,
    AllowAlways
}
