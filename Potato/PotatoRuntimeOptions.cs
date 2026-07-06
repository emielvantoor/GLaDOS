internal sealed class PotatoRuntimeOptions
{
    public bool Verbose { get; init; }

    public string PromptDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "prompts");

    public bool UseCompiledDefaultPrompts { get; init; }

    public HashSet<string> AlwaysAllowedPermissionKeys { get; } = new(StringComparer.Ordinal);

    public static PotatoRuntimeOptions FromArgs(string[] args, PotatoAppSettings appSettings)
    {
        return new PotatoRuntimeOptions
        {
            Verbose = args.Any(arg =>
                          arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase) ||
                          arg.Equals("-v", StringComparison.OrdinalIgnoreCase)) ||
                      IsTruthy(Environment.GetEnvironmentVariable("POTATO_VERBOSE")),
            PromptDirectory = GetPromptDirectory(args),
            UseCompiledDefaultPrompts = appSettings.UseCompiledDefaultPrompts
        };
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

    private static bool IsTruthy(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is "1" or "true" or "yes" or "on";
    }
}

internal enum ToolPermissionChoice
{
    Deny,
    AllowOnce,
    AllowAlways
}
