using System.Text.RegularExpressions;

namespace Potato;

internal sealed class FileMentionExpander
{
    private const int MaxAttachedFileCharacters = 120_000;
    private static readonly Regex FileMentionRegex = new(@"(?:^|\s)@(?<path>""[^""]+""|'[^']+'|\S+)", RegexOptions.Compiled);

    public string Expand(string input)
    {
        var attachments = new List<string>();
        foreach (Match match in FileMentionRegex.Matches(input))
        {
            string rawPath = match.Groups["path"].Value.Trim().Trim('"', '\'');
            string? resolvedPath = PathResolver.ResolveMentionedPath(rawPath);
            if (resolvedPath is null)
            {
                attachments.Add($"Could not resolve @{rawPath}.");
                continue;
            }

            if (!File.Exists(resolvedPath))
            {
                attachments.Add($"File not found: {resolvedPath}");
                continue;
            }

            try
            {
                string content = File.ReadAllText(resolvedPath);
                string truncationNotice = string.Empty;
                if (content.Length > MaxAttachedFileCharacters)
                {
                    content = content[..MaxAttachedFileCharacters];
                    truncationNotice = $"\n[Truncated after {MaxAttachedFileCharacters:N0} characters.]";
                }

                attachments.Add(
                    $"--- begin file: {resolvedPath} ---\n" +
                    content +
                    truncationNotice +
                    $"\n--- end file: {resolvedPath} ---");
            }
            catch (Exception ex)
            {
                attachments.Add($"Could not read {resolvedPath}: {ex.Message}");
            }
        }

        if (attachments.Count == 0)
        {
            return input;
        }

        PotatoConsole.WriteStatus($"Attached {attachments.Count} file reference(s).");
        return input + "\n\nAttached file context:\n" + string.Join("\n\n", attachments);
    }
}