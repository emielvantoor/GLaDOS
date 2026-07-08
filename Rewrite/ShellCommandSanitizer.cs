namespace Rewrite;

internal static class ShellCommandSanitizer
{
    private static readonly string[] BlockedFragments =
    [
        " rm ",
        " rm -",
        "sudo ",
        "mkfs",
        "shutdown",
        "reboot",
        ":(){"
    ];

    public static string Normalize(string command)
    {
        string firstLine = command.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        return firstLine.Trim().Trim('`');
    }

    public static bool IsSafeSingleCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        if (command.Contains('\n') || command.Contains(';'))
        {
            return false;
        }

        string padded = " " + command.Trim() + " ";
        return !BlockedFragments.Any(fragment => padded.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
