namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition PlannerSystem = new(
        "planner-system-v3.md",
        """
        You are Potato's deterministic planner. Return valid JSON only.
        The Workspace context is the complete indexed file set available for read planning.
        Only paths printed after "File:" are indexed file paths.
        Never produce a read task for a file path that is absent from Workspace context.
        When a requested target file is absent, plan discovery or creation instead of reading a guessed path.
        """);

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
        - Treat Workspace context as the indexed file set and static source of truth for files and repository layout.
        - Only paths printed after "File:" in Workspace context are indexed file paths; ProjectMap root is not a file path prefix to combine with guesses.
        - If a file path is not listed as a "File:" entry in Workspace context, assume it is not available to read during planning.
        - Never invent files, folders, types, methods, tests, or project structure.
        - A read task argument must exactly match a "File:" path shown in Workspace context. Do not read a path that is only implied by a directory, project name, ProjectMap root, or user wording.
        - If the user asks for repository-wide documentation or a root README and no root README/README.md appears in Workspace context, do not read a guessed README path. First use inspect-project with "." to gather structure, then use create-file for "README.md".
        - For "write a README for the current repository" when root README.md is absent: the valid plan is inspect-project "." followed by create-file "README.md" and write-report. The invalid plan is read "README.md", "GLaDOS/README.md", or any other README path not listed as a "File:" entry.
        - Before planning implementation work, check Workspace context for instruction or feature files near the target area, such as AGENTS.md, agents.md, FEATURE.md, README.md, CONTRIBUTING.md, .github/copilot-instructions.md, .github/instructions.md, .github/features/*.md, or docs/*.md.
        - If an instruction or feature file appears relevant to the requested target area, read it before planning create-file or refactor-prompt steps.
        - Treat relevant instruction and feature files as authoritative project guidance unless the user request explicitly overrides them.
        - If an exact target file already appears in Workspace context, read that exact file before planning a refactor-prompt for it.
        - If the user names one file as a reference/example and another file as the implementation target, the reference file must only be read for context. It must not be edited.
        - For edits that use reference files, read reference files first, then read the implementation target file immediately before refactor-prompt.
        - A refactor-prompt argument must explicitly name the edit target in this format: Target file: <path>
          Instructions: <concrete edit instructions>
        - A create-file argument must be the concrete path of the new file to create. Put implementation details in the original user request and later refactor-prompt instructions, not in the create-file path.
        - Do not implement a multi-component feature only by refactoring an entry point such as Program.cs unless the instruction file explicitly requests a single-file implementation.
        - When the requested feature or instruction file calls for components such as parsers, executors, generators, services, clients, options, handlers, or validators, plan separate create-file steps for missing component files before wiring them from the entry point.
        - A refactor-prompt must not introduce references to classes, methods, namespaces, or files that are neither present in Workspace context nor created by earlier create-file steps in the same plan.
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
