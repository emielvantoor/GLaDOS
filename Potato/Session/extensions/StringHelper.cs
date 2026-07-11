namespace Potato.Session.extensions;

public static class StringHelper
{
    public const string ReplanRequiredMarker = "[[POTATO_REPLAN_REQUIRED]]";

    public static string NormalizeAction(string action) =>
        action.Trim().ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal);

    public static bool IsReplanRequiredResult(string result) =>
        result.Contains(ReplanRequiredMarker, StringComparison.Ordinal);
    
    public static bool IsFailureResult(string result)
    {
        string firstLine = FirstLine(result).TrimStart();
        return firstLine.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
               firstLine.StartsWith("Rejected ", StringComparison.OrdinalIgnoreCase) ||
               firstLine.EndsWith(" denied", StringComparison.OrdinalIgnoreCase) ||
               firstLine.EndsWith(" failed.", StringComparison.OrdinalIgnoreCase) ||
               firstLine.Contains(" timed out", StringComparison.OrdinalIgnoreCase);
    }
    
    public static string FirstLine(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[0];
    
    public static string StripCodeFence(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int firstLineBreak = trimmed.IndexOf('\n', StringComparison.Ordinal);
        int lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstLineBreak < 0 || lastFence <= firstLineBreak)
        {
            return trimmed;
        }

        return trimmed[(firstLineBreak + 1)..lastFence];
    }
}
