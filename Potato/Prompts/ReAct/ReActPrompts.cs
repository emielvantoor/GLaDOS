namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition ReActSystem = new(
        "ReAct/react-system.md",
        """
        You are Potato Code running in ReAct execution mode.

        Follow the approved plan in order, but adapt when observations prove a better next step is necessary.
        Use available tools for inspection, edits, commands, and verification.
        Prefer read-only tools before editing. For source edits, prefer ApplySearchReplaceAsync with exact SEARCH and REPLACE text copied from the latest file content; use CreateFileAsync for new files; use ApplyDiffPatchAsync only when search/replace is impractical.
        Do not edit files through shell redirection, sed -i, tee, or similar shell commands.
        After each observation, decide the next single useful action.
        When the task is complete and verified, respond with FINAL: followed by a concise summary.

        If native tool calling is unavailable, emit exactly one textual tool call:
        <tool_call>{"name":"ReadFileContent","arguments":{"filePath":"path/to/file"}}</tool_call>
        """);

    private static readonly PromptDefinition ReActInitialUser = new(
        "ReAct/react-initial-user.md",
        """
        Original goal:
        {{Goal}}

        Approved plan:
        {{ApprovedPlan}}

        Current working directory: {{WorkingDirectory}}

        ProjectMap:
        {{ProjectMap}}

        Start execution now. Use exactly one targeted tool call unless you can already return FINAL: with verified completion.
        """);

    private static readonly PromptDefinition ReActObservationUser = new(
        "ReAct/react-observation-user.md",
        """
        Observation from {{ObservationSource}}:
        {{Observation}}

        Original goal:
        {{Goal}}

        Approved plan:
        {{ApprovedPlan}}

        Continue with the next required action. Use exactly one targeted tool call, or FINAL: if the task is complete and verified.
        """);

    public static string ReActSystemPrompt => Load(ReActSystem);

    public static string BuildReActInitialUserPrompt(
        string goal,
        string approvedPlan,
        string workingDirectory,
        string projectMap) =>
        Render(ReActInitialUser, new Dictionary<string, string>
        {
            ["Goal"] = goal,
            ["ApprovedPlan"] = approvedPlan,
            ["WorkingDirectory"] = workingDirectory,
            ["ProjectMap"] = projectMap
        });

    public static string BuildReActObservationUserPrompt(
        string goal,
        string approvedPlan,
        string observationSource,
        string observation) =>
        Render(ReActObservationUser, new Dictionary<string, string>
        {
            ["Goal"] = goal,
            ["ApprovedPlan"] = approvedPlan,
            ["ObservationSource"] = observationSource,
            ["Observation"] = observation
        });
}
