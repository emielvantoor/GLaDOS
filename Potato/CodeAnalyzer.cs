using System.Text;
using System.Text.RegularExpressions;

namespace Potato;

/// <summary>
/// Extracts public method signatures and type information from code files.
/// Uses regex-based parsing (logic helpers only, no LM involvement).
/// Provides a quick API overview before showing full minified code.
/// </summary>
internal sealed class CodeAnalyzer
{
    /// <summary>
    /// Extract public method signatures from code content.
    /// Returns a formatted string showing the public API.
    /// </summary>
    public static string ExtractPublicApi(string content, string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        
        return ext switch
        {
            ".cs" => ExtractCSharpApi(content),
            ".java" => ExtractJavaApi(content),
            ".cpp" or ".cc" or ".cxx" or ".h" or ".hpp" => ExtractCppApi(content),
            ".py" => ExtractPythonApi(content),
            ".js" or ".jsx" or ".ts" or ".tsx" => ExtractJavaScriptApi(content),
            ".go" => ExtractGoApi(content),
            ".rs" => ExtractRustApi(content),
            ".rb" => ExtractRubyApi(content),
            _ => ""
        };
    }

    private static string ExtractCSharpApi(string content)
    {
        var result = new StringBuilder();
        result.AppendLine("=== PUBLIC API ===");

        // Extract public classes
        var classMatches = Regex.Matches(
            content,
            @"(?:public\s+(?:sealed\s+|abstract\s+|partial\s+)*)?(?:class|interface|record|struct|enum)\s+(\w+)",
            RegexOptions.Multiline);

        foreach (Match match in classMatches)
        {
            result.AppendLine($"  {match.Groups[1].Value}");
        }

        // Extract public methods
        var methodMatches = Regex.Matches(
            content,
            @"public\s+(?:static\s+)?(?:async\s+)?(\w+[\?\[\]]*)\s+(\w+)\s*\((.*?)\)",
            RegexOptions.Multiline | RegexOptions.Singleline);

        if (methodMatches.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("PUBLIC METHODS:");
            foreach (Match match in methodMatches)
            {
                var returnType = match.Groups[1].Value.Trim();
                var methodName = match.Groups[2].Value;
                var parameters = SimplifyParameters(match.Groups[3].Value);
                result.AppendLine($"    {returnType} {methodName}({parameters})");
            }
        }

        // Extract public properties
        var propMatches = Regex.Matches(
            content,
            @"public\s+(?:sealed\s+)?(?:abstract\s+)?(?:virtual\s+)?(\w+[\?\[\]]*)\s+(\w+)\s*(?:\{|;|=>)",
            RegexOptions.Multiline);

        if (propMatches.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("PUBLIC PROPERTIES:");
            foreach (Match match in propMatches)
            {
                var type = match.Groups[1].Value.Trim();
                var propName = match.Groups[2].Value;
                result.AppendLine($"    {type} {propName}");
            }
        }

        return result.Length > 1 ? result.ToString() : "";
    }

    private static string ExtractJavaApi(string content)
    {
        var result = new StringBuilder();
        result.AppendLine("=== PUBLIC API ===");

        // Extract public classes
        var classMatches = Regex.Matches(
            content,
            @"public\s+(?:abstract\s+)?(?:final\s+)?(class|interface|enum)\s+(\w+)",
            RegexOptions.Multiline);

        foreach (Match match in classMatches)
        {
            result.AppendLine($"  {match.Groups[2].Value}");
        }

        // Extract public methods
        var methodMatches = Regex.Matches(
            content,
            @"public\s+(?:static\s+)?(?:final\s+)?(\w+[\[\]]*)\s+(\w+)\s*\((.*?)\)",
            RegexOptions.Multiline | RegexOptions.Singleline);

        if (methodMatches.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("PUBLIC METHODS:");
            foreach (Match match in methodMatches)
            {
                var returnType = match.Groups[1].Value.Trim();
                var methodName = match.Groups[2].Value;
                var parameters = SimplifyParameters(match.Groups[3].Value);
                result.AppendLine($"    {returnType} {methodName}({parameters})");
            }
        }

        return result.Length > 1 ? result.ToString() : "";
    }

    private static string ExtractCppApi(string content)
    {
        var result = new StringBuilder();
        result.AppendLine("=== PUBLIC API ===");

        // Look for class definitions and public sections
        var classMatches = Regex.Matches(
            content,
            @"(?:class|struct)\s+(\w+)",
            RegexOptions.Multiline);

        foreach (Match match in classMatches)
        {
            result.AppendLine($"  {match.Groups[1].Value}");
        }

        // Extract public methods (simple heuristic after 'public:')
        var publicSectionIdx = content.IndexOf("public:");
        if (publicSectionIdx >= 0)
        {
            var publicSection = content.Substring(publicSectionIdx, Math.Min(5000, content.Length - publicSectionIdx));
            var methodMatches = Regex.Matches(
                publicSection,
                @"(?:virtual\s+)?(\w+[\*&]*)\s+(\w+)\s*\((.*?)\)\s*(?:const)?(?:\{|;)",
                RegexOptions.Multiline);

            if (methodMatches.Count > 0)
            {
                result.AppendLine();
                result.AppendLine("PUBLIC METHODS:");
                foreach (Match match in methodMatches)
                {
                    var returnType = match.Groups[1].Value.Trim();
                    var methodName = match.Groups[2].Value;
                    var parameters = SimplifyParameters(match.Groups[3].Value);
                    result.AppendLine($"    {returnType} {methodName}({parameters})");
                }
            }
        }

        return result.Length > 1 ? result.ToString() : "";
    }

    private static string ExtractPythonApi(string content)
    {
        var result = new StringBuilder();
        result.AppendLine("=== PUBLIC API ===");

        // Extract class definitions
        var classMatches = Regex.Matches(
            content,
            @"^class\s+(\w+)",
            RegexOptions.Multiline);

        foreach (Match match in classMatches)
        {
            result.AppendLine($"  {match.Groups[1].Value}");
        }

        // Extract public methods (not starting with _)
        var methodMatches = Regex.Matches(
            content,
            @"^\s+def\s+(?!_)(\w+)\s*\((.*?)\):",
            RegexOptions.Multiline);

        if (methodMatches.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("PUBLIC METHODS:");
            foreach (Match match in methodMatches)
            {
                var methodName = match.Groups[1].Value;
                var parameters = SimplifyParameters(match.Groups[2].Value);
                result.AppendLine($"    def {methodName}({parameters})");
            }
        }

        // Extract module-level functions (not starting with _)
        var funcMatches = Regex.Matches(
            content,
            @"^def\s+(?!_)(\w+)\s*\((.*?)\):",
            RegexOptions.Multiline);

        if (funcMatches.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("PUBLIC FUNCTIONS:");
            foreach (Match match in funcMatches)
            {
                var funcName = match.Groups[1].Value;
                var parameters = SimplifyParameters(match.Groups[2].Value);
                result.AppendLine($"    def {funcName}({parameters})");
            }
        }

        return result.Length > 1 ? result.ToString() : "";
    }

    private static string ExtractJavaScriptApi(string content)
    {
        var result = new StringBuilder();
        result.AppendLine("=== PUBLIC API ===");

        // Extract exported functions/classes
        var exportMatches = Regex.Matches(
            content,
            @"export\s+(?:default\s+)?(?:async\s+)?(?:function|class|\w+)\s+(\w+)",
            RegexOptions.Multiline);

        if (exportMatches.Count > 0)
        {
            result.AppendLine("EXPORTS:");
            foreach (Match match in exportMatches)
            {
                result.AppendLine($"    {match.Groups[1].Value}");
            }
        }

        // Extract public methods in classes
        var methodMatches = Regex.Matches(
            content,
            @"(?:async\s+)?(\w+)\s*\((.*?)\)\s*(?:\{|=>)",
            RegexOptions.Multiline | RegexOptions.Singleline);

        if (methodMatches.Count > 0 && methodMatches.Count <= 20)
        {
            result.AppendLine();
            result.AppendLine("METHODS:");
            foreach (Match match in methodMatches)
            {
                var methodName = match.Groups[1].Value;
                if (methodName.Length > 2 && char.IsLower(methodName[0]))
                    continue; // Skip lowercase names
                var parameters = SimplifyParameters(match.Groups[2].Value);
                result.AppendLine($"    {methodName}({parameters})");
            }
        }

        return result.Length > 1 ? result.ToString() : "";
    }

    private static string ExtractGoApi(string content)
    {
        var result = new StringBuilder();
        result.AppendLine("=== PUBLIC API ===");

        // Extract exported functions (start with capital letter)
        var funcMatches = Regex.Matches(
            content,
            @"func\s+\(?\w*\)?\s+([A-Z]\w+)\s*\((.*?)\)",
            RegexOptions.Multiline);

        if (funcMatches.Count > 0)
        {
            result.AppendLine("EXPORTED:");
            foreach (Match match in funcMatches)
            {
                var funcName = match.Groups[1].Value;
                var parameters = SimplifyParameters(match.Groups[2].Value);
                result.AppendLine($"    {funcName}({parameters})");
            }
        }

        return result.Length > 1 ? result.ToString() : "";
    }

    private static string ExtractRustApi(string content)
    {
        var result = new StringBuilder();
        result.AppendLine("=== PUBLIC API ===");

        // Extract public structs
        var structMatches = Regex.Matches(
            content,
            @"pub\s+struct\s+(\w+)",
            RegexOptions.Multiline);

        foreach (Match match in structMatches)
        {
            result.AppendLine($"  struct {match.Groups[1].Value}");
        }

        // Extract public functions
        var funcMatches = Regex.Matches(
            content,
            @"pub\s+(?:async\s+)?(?:unsafe\s+)?(?:extern\s+)?fn\s+(\w+)\s*\((.*?)\)",
            RegexOptions.Multiline);

        if (funcMatches.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("PUBLIC FUNCTIONS:");
            foreach (Match match in funcMatches)
            {
                var funcName = match.Groups[1].Value;
                var parameters = SimplifyParameters(match.Groups[2].Value);
                result.AppendLine($"    fn {funcName}({parameters})");
            }
        }

        return result.Length > 1 ? result.ToString() : "";
    }

    private static string ExtractRubyApi(string content)
    {
        var result = new StringBuilder();
        result.AppendLine("=== PUBLIC API ===");

        // Extract class definitions
        var classMatches = Regex.Matches(
            content,
            @"^class\s+(\w+)",
            RegexOptions.Multiline);

        foreach (Match match in classMatches)
        {
            result.AppendLine($"  {match.Groups[1].Value}");
        }

        // Extract public methods (not starting with _)
        var methodMatches = Regex.Matches(
            content,
            @"^\s+def\s+(?!_)(\w+)\s*\((.*?)\)",
            RegexOptions.Multiline);

        if (methodMatches.Count > 0)
        {
            result.AppendLine();
            result.AppendLine("PUBLIC METHODS:");
            foreach (Match match in methodMatches)
            {
                var methodName = match.Groups[1].Value;
                var parameters = SimplifyParameters(match.Groups[2].Value);
                result.AppendLine($"    def {methodName}({parameters})");
            }
        }

        return result.Length > 1 ? result.ToString() : "";
    }

    private static string SimplifyParameters(string parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
            return "";

        // Simplify parameter list by removing default values and types (show names only for brevity)
        var parts = parameters.Split(',');
        var simplified = new List<string>();

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            // Extract just the parameter name (after type/modifiers)
            var lastSpace = trimmed.LastIndexOf(' ');
            if (lastSpace > 0)
                trimmed = trimmed.Substring(lastSpace + 1).Trim();

            // Remove default values
            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx > 0)
                trimmed = trimmed.Substring(0, eqIdx).Trim();

            simplified.Add(trimmed);
        }

        return string.Join(", ", simplified);
    }
}
