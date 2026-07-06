internal sealed class PotatoRuntimeOptions
{
    public bool Verbose { get; init; }

    public HashSet<string> AlwaysAllowedPermissionKeys { get; } = new(StringComparer.Ordinal);

    public static PotatoRuntimeOptions FromArgs(string[] args)
    {
        return new PotatoRuntimeOptions
        {
            Verbose = args.Any(arg =>
                          arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase) ||
                          arg.Equals("-v", StringComparison.OrdinalIgnoreCase)) ||
                      IsTruthy(Environment.GetEnvironmentVariable("POTATO_VERBOSE"))
        };
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
