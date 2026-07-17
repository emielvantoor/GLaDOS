using System.Text;
using System.Text.RegularExpressions;

namespace Potato;

/// <summary>
/// Deterministic code minification using regex and string manipulation.
/// Removes comments and collapses whitespace, including line breaks, where that is safe.
/// Uses logic helpers only - no LM involvement.
/// </summary>
internal sealed class CodeMinifier
{
    /// <summary>
    /// Detect if a file is code based on extension.
    /// </summary>
    public static bool IsCodeFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            // C# / .NET
            ".cs" => true,
            // C++
            ".cpp" or ".cc" or ".cxx" or ".h" or ".hpp" => true,
            // Java
            ".java" => true,
            // Python
            ".py" => true,
            // JavaScript / TypeScript
            ".js" or ".ts" or ".jsx" or ".tsx" => true,
            // Go
            ".go" => true,
            // Rust
            ".rs" => true,
            // Ruby
            ".rb" => true,
            // Markup
            ".html" or ".xml" or ".svg" => true,
            // Styles
            ".css" or ".scss" or ".less" => true,
            // Config
            ".json" or ".yaml" or ".yml" or ".toml" => true,
            // SQL
            ".sql" => true,
            _ => false
        };
    }

    /// <summary>
    /// Minify code content for a given file type.
    /// Returns minified content with reduced comments and whitespace.
    /// </summary>
    public static string Minify(string content, string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        
        return ext switch
        {
            ".cs" => MinifyCSharp(content),
            ".cpp" or ".cc" or ".cxx" or ".h" or ".hpp" => MinifyCpp(content),
            ".java" => MinifyJava(content),
            ".py" => MinifyPython(content),
            ".js" or ".jsx" => MinifyJavaScript(content),
            ".ts" or ".tsx" => MinifyTypeScript(content),
            ".go" => MinifyGo(content),
            ".rs" => MinifyRust(content),
            ".rb" => MinifyRuby(content),
            ".html" or ".xml" or ".svg" => MinifyXml(content),
            ".css" => MinifyCss(content),
            ".json" => MinifyJson(content),
            ".yaml" or ".yml" => MinifyYaml(content),
            ".sql" => MinifySql(content),
            _ => RemoveComments(content, ext)
        };
    }

    private static string MinifyCSharp(string content)
    {
        // Remove XML documentation comments
        content = Regex.Replace(content, @"^\s*///.*$", "", RegexOptions.Multiline);
        // Remove single-line comments
        content = Regex.Replace(content, @"//.*$", "", RegexOptions.Multiline);
        // Remove multi-line comments
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return CollapseWhitespace(content);
    }

    private static string MinifyCpp(string content)
    {
        // Remove single-line comments
        content = Regex.Replace(content, @"//.*$", "", RegexOptions.Multiline);
        // Remove multi-line comments
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
        // Remove preprocessor directives (optional, keep for now as they matter)
        return CollapseWhitespace(content);
    }

    private static string MinifyJava(string content)
    {
        // Remove JavaDoc comments
        content = Regex.Replace(content, @"/\*\*.*?\*/", "", RegexOptions.Singleline);
        // Remove single-line comments
        content = Regex.Replace(content, @"//.*$", "", RegexOptions.Multiline);
        // Remove multi-line comments
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return CollapseWhitespace(content);
    }

    private static string MinifyPython(string content)
    {
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var result = new StringBuilder();
        bool inTripleQuote = false;
        string tripleQuoteChar = "";

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Track triple-quoted strings
            if (trimmed.StartsWith("\"\"\"") || trimmed.StartsWith("'''"))
            {
                tripleQuoteChar = trimmed.StartsWith("\"\"\"") ? "\"\"\"" : "'''";
                inTripleQuote = !inTripleQuote;
                continue;
            }

            // Skip docstrings
            if (inTripleQuote)
                continue;

            // Skip comments (but not shebang)
            if (trimmed.StartsWith("#") && !trimmed.StartsWith("#!"))
                continue;

            // Remove inline comments
            int commentIdx = trimmed.IndexOf('#');
            if (commentIdx > 0)
                trimmed = trimmed.Substring(0, commentIdx).TrimEnd();

            if (!string.IsNullOrWhiteSpace(trimmed))
                result.AppendLine(trimmed);
        }

        return result.ToString().Trim();
    }

    private static string MinifyJavaScript(string content)
    {
        // Remove single-line comments (be careful with URLs)
        content = Regex.Replace(content, @"(?<!:)//(?!/).*$", "", RegexOptions.Multiline);
        // Remove multi-line comments
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return CollapseWhitespace(content);
    }

    private static string MinifyTypeScript(string content)
    {
        // Same as JavaScript for now
        return MinifyJavaScript(content);
    }

    private static string MinifyGo(string content)
    {
        // Remove single-line comments
        content = Regex.Replace(content, @"//.*$", "", RegexOptions.Multiline);
        // Remove multi-line comments
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return CollapseWhitespace(content);
    }

    private static string MinifyRust(string content)
    {
        // Remove single-line comments
        content = Regex.Replace(content, @"//.*$", "", RegexOptions.Multiline);
        // Remove doc comments
        content = Regex.Replace(content, @"///.*$", "", RegexOptions.Multiline);
        // Remove multi-line comments (basic; nested not handled)
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return CollapseWhitespace(content);
    }

    private static string MinifyRuby(string content)
    {
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var result = new StringBuilder();
        bool inMultiComment = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Track =begin...=end blocks
            if (trimmed.StartsWith("=begin"))
            {
                inMultiComment = true;
                continue;
            }
            if (trimmed.StartsWith("=end"))
            {
                inMultiComment = false;
                continue;
            }

            if (inMultiComment)
                continue;

            // Skip comments
            if (trimmed.StartsWith("#"))
                continue;

            // Remove inline comments
            int commentIdx = trimmed.IndexOf('#');
            if (commentIdx > 0)
                trimmed = trimmed.Substring(0, commentIdx).TrimEnd();

            if (!string.IsNullOrWhiteSpace(trimmed))
                result.AppendLine(trimmed);
        }

        return result.ToString().Trim();
    }

    private static string MinifyXml(string content)
    {
        // Remove XML comments
        content = Regex.Replace(content, @"<!--.*?-->", "", RegexOptions.Singleline);
        // Collapse whitespace between tags
        content = Regex.Replace(content, @">\s+<", "><");
        return content.Trim();
    }

    private static string MinifyCss(string content)
    {
        // Remove single-line comments
        content = Regex.Replace(content, @"//.*$", "", RegexOptions.Multiline);
        // Remove multi-line comments
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
        // Collapse whitespace
        content = Regex.Replace(content, @"\s+", " ");
        return content.Trim();
    }

    private static string MinifyJson(string content)
    {
        // JSON doesn't support comments, but we collapse whitespace
        content = Regex.Replace(content, @"\s+", " ");
        content = Regex.Replace(content, @":\s+", ":");
        content = Regex.Replace(content, @",\s+", ",");
        return content.Trim();
    }

    private static string MinifyYaml(string content)
    {
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var result = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            // Skip comments
            if (trimmed.StartsWith("#"))
                continue;

            // Remove inline comments (be careful with colons)
            int commentIdx = trimmed.IndexOf('#');
            if (commentIdx > 0 && trimmed[commentIdx - 1] != ':')
                trimmed = trimmed.Substring(0, commentIdx).TrimEnd();

            if (!string.IsNullOrWhiteSpace(trimmed))
                result.AppendLine(trimmed);
        }

        string minified = result.ToString();
        minified = Regex.Replace(minified, @"\n\s*\n(\s*\n)+", "\n\n", RegexOptions.Multiline);
        return minified.Trim();
    }

    private static string MinifySql(string content)
    {
        // Remove single-line comments
        content = Regex.Replace(content, @"--.*$", "", RegexOptions.Multiline);
        // Remove multi-line comments
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return CollapseWhitespace(content);
    }

    private static string RemoveComments(string content, string ext)
    {
        // Fallback: try to remove common comment patterns
        content = Regex.Replace(content, @"//.*$", "", RegexOptions.Multiline);
        content = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
        content = Regex.Replace(content, @"#.*$", "", RegexOptions.Multiline);
        return CollapseWhitespace(content);
    }

    private static string CollapseWhitespace(string content)
    {
        content = Regex.Replace(content, @"\s+", " ");
        return content.Trim();
    }
}
