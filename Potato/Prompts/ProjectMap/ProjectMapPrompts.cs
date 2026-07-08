namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    public static string BuildProjectMapSystemPrompt =>
        "You summarize source and project files for a repository map. Return concise bullets only.";
    
    public static string BuildProjectMapUserPrompt(string filePath, string fileContent) =>
        $$$"""
           Summarize this source or project file for a repository map.
           Return under 3 bullet points.
           Include the file's purpose, programming language or project type, and key public methods/types/components/configuration.
           Do not include markdown fences or large code quotes.

           File path:
           {{{filePath}}}

           File contents:
           ```
           {{{fileContent}}}
           ```
           """;
}