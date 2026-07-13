using Potato.Session.extensions;

namespace Potato.Session;

internal static class PlanningJsonExtractor
{
    public static string ExtractJsonObject(string text)
    {
        string trimmed = StringHelper.StripCodeFence(text).Trim();
        int start = trimmed.IndexOf('{', StringComparison.Ordinal);
        int end = trimmed.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("Model did not return a JSON object.");
        }

        return trimmed[start..(end + 1)];
    }

    public static string ExtractJsonArray(string text)
    {
        string trimmed = StringHelper.StripCodeFence(text).Trim();
        int start = trimmed.IndexOf('[', StringComparison.Ordinal);
        int end = trimmed.LastIndexOf(']');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("Planner did not return a JSON array.");
        }

        return trimmed[start..(end + 1)];
    }
}
