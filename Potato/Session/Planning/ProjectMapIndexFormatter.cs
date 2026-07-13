using System.Text;

namespace Potato.Session;

internal static class ProjectMapIndexFormatter
{
    public static string BuildWorkspacePlanningContext(string workspaceContext, string currentDirectory)
    {
        var builder = new StringBuilder();
        foreach (string line in workspaceContext.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            builder.AppendLine(line);
            if (line.StartsWith("ProjectMap root:", StringComparison.Ordinal))
            {
                string projectMapRoot = line["ProjectMap root:".Length..].Trim();
                builder.AppendLine($"Current working folder: {FormatCurrentWorkingFolder(projectMapRoot, currentDirectory)}");
            }
        }

        return builder.ToString();
    }

    private static string FormatCurrentWorkingFolder(string projectMapRoot, string currentDirectory)
    {
        try
        {
            string relativePath = Path.GetRelativePath(projectMapRoot, currentDirectory)
                .Replace('\\', '/');

            return string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath;
        }
        catch
        {
            return currentDirectory;
        }
    }
}
