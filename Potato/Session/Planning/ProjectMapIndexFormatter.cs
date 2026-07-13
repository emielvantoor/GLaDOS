using System.Text;

namespace Potato.Session;

internal static class ProjectMapIndexFormatter
{
    public static string BuildWorkspaceFileIndex(string workspaceContext)
    {
        var builder = new StringBuilder();
        foreach (string line in workspaceContext.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("ProjectMap root:", StringComparison.Ordinal) ||
                line.StartsWith("File: ", StringComparison.Ordinal))
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString();
    }
}
