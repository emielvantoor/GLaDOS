using System.Text.RegularExpressions;

namespace Rewrite;

internal static partial class CommandTemplates
{
    public static bool TryTranslate(string wise, out string? command)
    {
        command = null;
        string input = wise.Trim();

        Match contentSearch = ContentSearchRegex().Match(input);
        if (contentSearch.Success)
        {
            string text = ShellQuote(contentSearch.Groups["text"].Value.Trim());
            command = $"grep -rl {text} .";
            return true;
        }

        if (ListFilesRegex().IsMatch(input))
        {
            command = "find . -maxdepth 1 -type f";
            return true;
        }

        return false;
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    [GeneratedRegex(@"content\s+(contains|including|with)\s+(?<text>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ContentSearchRegex();

    [GeneratedRegex(@"\b(list|show)\b.*\bfiles\b", RegexOptions.IgnoreCase)]
    private static partial Regex ListFilesRegex();
}
