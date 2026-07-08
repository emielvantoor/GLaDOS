namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition PlannerSystem = new(
        "planner-system-v3.md",
        "You are Potato's deterministic planner. Return valid JSON only.");

    private static readonly PromptDefinition PlannerUser = new(
        "planner-user.md",
        """
        You are the Architect/Planner phase of Potato.
        Return exactly one strict JSON array and nothing else.

        User request:
        {{UserRequest}}

        Workspace context:
        {{WorkspaceContext}}

        Supported actions:
        {{SupportedActions}}

        Rules:
        - Treat Workspace context as the static source of truth for files and repository layout.
        - Never invent files, folders, types, methods, tests, or project structure.
        - Detect the requested project area, programming language, framework, or layer from the User request and Workspace context before planning edits.
        - After detecting the target area, continue only within that language, framework, or layer unless the user explicitly asks for a cross-stack change.
        - Do not refactor frontend files when the request targets backend code, and do not refactor backend files when the request targets frontend code.
        - If the target area is ambiguous, first read the most relevant manifest, README, or source file, then use write-report to explain the ambiguity instead of editing unrelated code.
        - Produce a deterministic, linear plan for the executor.
        - Every task must contain exactly these properties: Step, Action, Argument, Reason.
        - Step must be a sequential integer starting at 1.
        - Action must be one of the supported actions listed above.
        {{PlanningGuidance}}

        Example:
        [
          {"Step":1,"Action":"read","Argument":"src/backend/session.ts","Reason":"Inspect the backend session code requested by the user."},
          {"Step":2,"Action":"refactor-prompt","Argument":"Update only the backend TypeScript session flow described in the user request.","Reason":"Apply the requested change within the detected backend TypeScript area."},
          {"Step":3,"Action":"write-report","Argument":"Summarize the backend TypeScript change and any verification result.","Reason":"Give the user a natural final report."}
        ]
        """);

    public static string PlannerSystemPrompt => Load(PlannerSystem);

    public static string BuildPlannerUserPrompt(
        string userRequest,
        string workspaceContext,
        IReadOnlyCollection<string> supportedActions,
        IReadOnlyCollection<string> planningGuidance) =>
        Render(PlannerUser, new Dictionary<string, string>
        {
            ["UserRequest"] = userRequest,
            ["WorkspaceContext"] = workspaceContext,
            ["SupportedActions"] = BuildSupportedActionList(supportedActions),
            ["PlanningGuidance"] = BuildPlanningGuidance(planningGuidance)
        });

    private static string BuildSupportedActionList(IReadOnlyCollection<string> supportedActions) =>
        string.Join(Environment.NewLine, supportedActions.Select(action => $"- {action}"));

    private static string BuildPlanningGuidance(IReadOnlyCollection<string> planningGuidance) =>
        string.Join(Environment.NewLine, planningGuidance.Select(guidance => $"- {guidance}"));
}
