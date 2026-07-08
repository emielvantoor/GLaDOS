internal static class PotatoPrompts
{
    public static string GetPlannerPrompt(string userRequest, string ProjectMap) =>
        $$$"""
        You are the Architect/Planner phase of Potato.
        Return exactly one strict JSON array and nothing else.

        User request:
        {{{userRequest}}}

        ProjectMap:
        {{{ProjectMap}}}

        Rules:
        - Use ProjectMap as the static source of truth for files and repository layout.
        - Never invent files, folders, types, methods, tests, or project structure.
        - Produce a deterministic, linear plan for the Executor.
        - Every task must contain exactly these properties: Step, Action, Argument, Reason.
        - Step must be a sequential integer starting at 1.
        - Action must be one of: read, refactor_prompt, write_report.
        - Use read with an exact file path from ProjectMap.
        - Use refactor_prompt only after reading the file that should be changed.
        - For refactor_prompt, put only the concrete edit instructions in Argument.
        - Use write_report when the user should receive findings or a summary.

        Example:
        [
          {{"Step":1,"Action":"read","Argument":"Potato/PotatoSession.cs","Reason":"Inspect the executor loop that will be changed."}},
          {{"Step":2,"Action":"refactor_prompt","Argument":"Update the executor loop to handle read, refactor_prompt, and write_report actions.","Reason":"Apply the requested deterministic executor behavior."}},
          {{"Step":3,"Action":"write_report","Argument":"Summarize the files changed and verification result.","Reason":"Give the user a natural final report."}}
        ]
        """;

    public static string GetProjectMapPrompt(string filePath, string fileContent) =>
        $$$"""
        Summarize this C# file for a repository map.
        Return under 3 bullet points.
        Include the file's purpose and key public methods/types.
        Do not include markdown fences or large code quotes.

        File path:
        {{{filePath}}}

        File contents:
        ```csharp
        {{{fileContent}}}
        ```
        """;

    public static string GetRefactorPrompt(string filePath, string fileContent, string instructions) =>
        $$$"""
        You are the Refactor phase of Potato.
        Return exactly one SEARCH/REPLACE patch and nothing else.
        The SEARCH text must be copied exactly from the provided full file content.
        The SEARCH block must be large enough to match exactly once.
        The REPLACE block must contain the complete replacement text.
        If no safe exact patch can be made, return empty SEARCH and REPLACE blocks.

        Required format:
        <SEARCH>
        exact existing text
        </SEARCH>
        <REPLACE>
        complete replacement text
        </REPLACE>

        Target file:
        {{{filePath}}}

        Instructions:
        {{{instructions}}}

        Full file content:
        ```csharp
        {{{fileContent}}}
        ```
        """;
}
