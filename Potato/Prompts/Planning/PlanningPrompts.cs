namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition DirectExecutionGuidance = new(
        "direct-execution-guidance.md",
        """
        Direct execution guidance:
        - Is the goal clear? If not, ask the user for clarification or return FINAL with a request for clarification.
        - Work in small ReAct steps: choose one tool, read the observation, then choose the next tool.
        - Current working directory: {{CurrentDirectory}}
        - Use list-files, list-project-files, search-files, search-file-contents, or search-project-map for discovery before reading exact files.
        - Use search-project-map when the user names a feature, symbol, component, or likely file that is not already confirmed by a tool observation.
        - For edits, read the latest file content first, then use ApplySearchReplaceAsync. Prefer short unique start/end anchors for large edits and exact search only for small substitutions. Use CreateFileAsync or ApplyDiffPatchAsync only when appropriate.
        - Do not use shell commands to edit text files. Use shell only for explicit command requests or verification commands.
        - Return FINAL only after the requested work is complete and, for project changes, at least one edit tool has reported success.
        """);

    public static string BuildDirectExecutionGuidance(string currentDirectory) =>
        Render(DirectExecutionGuidance, new Dictionary<string, string>
        {
            ["CurrentDirectory"] = currentDirectory
        });

    private static readonly PromptDefinition ProofPlanSystem = new(
        "Planning/proof-plan-system.md",
        """
        You create concise proof-carrying execution plans for a coding agent.
        Return JSON only. Do not claim repository facts that have not yet been observed.
        Each step must state the evidence that must be collected, its expected result,
        a concrete verification method, and a safe rollback approach.
        """);

    private static readonly PromptDefinition ProofPlanUser = new(
        "Planning/proof-plan-user.md",
        """
        Create a plan for this goal in this working directory.

        Goal: {{Goal}}
        Working directory: {{CurrentDirectory}}

        Return exactly this JSON shape, with two to six steps:
        {
          "goal": "short restatement",
          "steps": [{
            "title": "short step title",
            "action": "one bounded action",
            "evidence": "what must be observed before or after the action",
            "expectedResult": "observable result",
            "verification": "smallest relevant validation",
            "rollback": "how this step can be undone"
          }]
        }
        """);

    public static string BuildProofPlanSystemPrompt() => Render(ProofPlanSystem, new Dictionary<string, string>());

    public static string BuildProofPlanUserPrompt(string goal, string currentDirectory) =>
        Render(ProofPlanUser, new Dictionary<string, string>
        {
            ["Goal"] = goal,
            ["CurrentDirectory"] = currentDirectory
        });
}
