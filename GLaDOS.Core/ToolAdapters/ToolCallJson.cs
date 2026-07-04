using System.Text.Json.Nodes;

namespace GLaDOS.Core.ToolAdapters;

internal static class ToolCallJson
{
    public static string NormalizeStringArgument(JsonNode? node)
    {
        var value = node?.ToString() ?? string.Empty;

        while (value.Length >= 2 &&
               value[0] == '"' &&
               value[^1] == '"')
        {
            try
            {
                var unwrapped = JsonNode.Parse(value)?.GetValue<string>();
                if (string.IsNullOrEmpty(unwrapped) || unwrapped == value)
                {
                    return value;
                }

                value = unwrapped;
            }
            catch
            {
                return value[1..^1];
            }
        }

        return value;
    }
}
