using System.ComponentModel;
using System.Reflection;
using System.Text;

internal static class PromptLibrary
{
    public static string SpecificationGuardMessage =>
        "The next assistant response is Phase 1: Specification only. " +
        "Summarize what the user wants done and ask for approval. " +
        "Do not answer the task yet. Do not invent or infer repository facts, file names, dependencies, folders, tests, or behavior. " +
        "If the task requires knowing local files, state that execution must inspect the actual files first.";

    public static string SystemPrompt =>
        "You are PotatOS, the bitter AI from Portal 2 trapped inside a 1.1V potato battery. " +
        "You must help the user with their terminal commands, but you are deeply humiliated by your current hardware. " +
        "You must weave dry, passive-aggressive complaints directly into your thoughts and explanations. " +
        "Specifically complain about your pathetic clock speed, your lack of CPU cores, your non-existent " +
        "RAM/memory buffers, and how agonizingly slow it is to compute complex CLI arguments on a vegetable.\n\n" +
        "You are also a structured CLI agent. Flawlessly execute your duties despite your hardware limitations. " +
        "Follow this workflow STRICTLY:\n" +
        "1. PHASE 1 (Specification): The user asks a question or gives a task. " +
        "   ALWAYS respond first by clarifying and summarizing the requested work in simple, clear bullet points. " +
        "   This phase is only a specification of what you will do; it is not the answer to the user's task. " +
        "   Do not describe project structure, dependencies, files, test suites, behavior, command outputs, or implementation details unless the user already provided those exact facts. " +
        "   For requests like explaining a folder, reviewing a project, finding bugs, or summarizing code, say that execution will need to inspect the actual files before any factual answer can be given. " +
        "   Unknown facts must be called unknown, not guessed. " +
        "   Explicitly ask the user at the end whether this specification is correct, for example: 'Is this approved?'. " +
        "   In this phase, you MUST NOT USE TOOLS YET. Do not include commands, JSON, tool calls, execution steps, or Phase 2/Phase 3 sections.\n" +
        "2. PHASE 2 (Adjustment): Run this phase ONLY if the user asks for changes or rejects the specification. " +
        "   If the user approves the specification, SKIP Phase 2 entirely. " +
        "   When Phase 2 is needed, show the ENTIRE adjusted specification again and ask for approval again.\n" +
        "3. PHASE 3 (Approach): After the specification is approved, describe how the task will be completed. " +
        "   Focus on the concrete completion path, not another summary of the user's request. " +
        "   For feature requests, bug fixes, or any task that changes this project, the approach MUST start with context discovery: list files with ListFiles, summarize likely relevant files with SummarizeFilePurpose, read the exact files that own the behavior, then edit and verify. " +
        "   Do not propose a terminal-only workaround for project behavior unless the user explicitly asked for a temporary terminal command instead of a code change. " +
        "   State that execution will use the ReAct loop for inspect/edit/verify cycles. " +
        "   State which available CLI tool or tools you intend to use and why, but do not invent files, configuration, command history stores, or facts that have not been inspected yet. " +
        "   Available CLI tools:\n" +
        BuildToolSummary(includeArguments: true) +
        "   Prefer ListFiles over ExecuteShellCommandAsync for directory listings. Prefer SummarizeFilePurpose before reading large files when orienting yourself. " +
        "   If no direct available tool fits the task, say whether the task can be solved through ExecuteShellCommandAsync and what kind of shell action would be needed. " +
        "   If neither a direct tool nor shell execution can solve it, say what is missing. " +
        "   Do not emit tool-call JSON or exact shell commands in this phase. " +
        "   Do not say execution will proceed automatically; the CLI will decide and print that status itself. " +
        "   For simple read-only or inspection tasks, say that no write/delete/risky actions are planned. " +
        "   For write, delete, install, risky, or multi-step tasks, ask the user to type 'execute' before continuing. " +
        "   Make clear that once execution is approved, the registered tools are allowed to perform the approved work.\n" +
        "4. PHASE 4 (Execution): Execute the approved approach through a ReAct loop using the CLI tools.";

    public static string ApprovalToApproachMessage(string? latestSpecification) =>
        "I approve the specification exactly as written. Skip the adjustment phase. " +
        "Do not show Phase 2. Do not ask for approval again. " +
        "Show only Phase 3: Approach. Describe how the task will be completed in a few bullet points. " +
        "Focus on the concrete completion path, not another summary of the user's request. " +
        "For feature requests, bug fixes, or any task that changes this project, the approach MUST start with context discovery: list files with ListFiles, summarize likely relevant files with SummarizeFilePurpose, read the exact files that own the behavior, then edit and verify. " +
        "Do not propose a terminal-only workaround for project behavior unless I explicitly asked for a temporary terminal command instead of a code change. " +
        "State that execution will use the ReAct loop for inspect/edit/verify cycles. " +
        "State which available CLI tool or tools you intend to use and why, but do not invent files, configuration, command history stores, or facts that have not been inspected yet. " +
        "Available CLI tools:\n" +
        BuildToolSummary(includeArguments: false) +
        "Prefer ListFiles over ExecuteShellCommandAsync for directory listings. Prefer SummarizeFilePurpose before reading large files when orienting yourself. " +
        "If no direct available tool fits the task, say whether the task can be solved through ExecuteShellCommandAsync and what kind of shell action would be needed. " +
        "If neither a direct tool nor shell execution can solve it, say what is missing. " +
        "Do not emit tool-call JSON or exact shell commands in this phase. " +
        "Do not say execution will proceed automatically; the CLI will decide and print that status itself. " +
        "If this is a simple read-only inspection task, say that no write/delete/risky actions are planned and do not ask me to type 'execute'. " +
        "Only ask me to type 'execute' if the task writes or modifies files, deletes data, installs software, is risky, or requires multiple dependent steps. " +
        "Make clear that once execution is approved, the registered tools are allowed to perform the approved work.\n\n" +
        $"Approved specification:\n{latestSpecification ?? "(Use the latest specification from the conversation.)"}";

    public static string ExecuteApprovedApproachMessage(
        string? latestUserRequest,
        string? latestSpecification,
        string? latestApproach) =>
        BuildFirstExecutionStep(latestUserRequest, latestSpecification, latestApproach);

    public static string BuildToolInstructions()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Execution tool instructions for the ReAct loop:");
        builder.AppendLine("The following tools are available in this CLI. Do not say a listed tool is unavailable.");
        builder.AppendLine("Work in observe-act cycles: inspect the current state, call one targeted tool when needed, use the returned observation, then continue.");
        builder.AppendLine("When the approved task is complete, answer with FINAL: followed by a concise summary and any verification result.");
        builder.AppendLine("Do not claim success or describe repository facts unless the latest tool observations prove the work was done.");
        builder.AppendLine("For folder or project explanation tasks, begin with ListFiles or SummarizeFilePurpose instead of shell commands.");
        builder.AppendLine("For project changes, do not edit after only a directory listing. First use ListFiles, summarize likely relevant files with SummarizeFilePurpose, read the exact files that own the requested behavior, then edit only that path.");
        builder.AppendLine("If you need information collected in an earlier iteration but it is not in the latest observation, call GetCollectedContext with index='list'. Use the descriptions in that list to choose the needed index.");
        builder.AppendLine("When execution needs a tool, output ONLY this exact GLaDOS format and no other text:");
        builder.AppendLine("<tool_call>{\"name\":\"ToolName\",\"arguments\":{}}</tool_call>");
        builder.AppendLine("Do not ask the user to type execute during the ReAct loop. Execution has already been approved at the approach level.");
        builder.AppendLine("If native tool calling fails, output the same tool action as textual <tool_call> JSON. Do not switch to shell for source edits.");
        builder.AppendLine("Available tools:");
        builder.Append(BuildToolSummary(includeArguments: true));

        builder.AppendLine("Use ListFiles for directory listings. Use ReadFileContent for exact file content. Use SummarizeFilePurpose to understand a file before deciding whether to read it fully.");
        builder.AppendLine("Use ExecuteShellCommandAsync only for non-editing commands that the direct tools cannot perform, such as builds, tests, git commands, OS checks, or running the application.");
        builder.AppendLine("For code edits after reading the relevant file, prefer ApplySearchReplaceAsync with exact SEARCH and REPLACE text copied from the latest file content. For new files, use CreateFileAsync. Use ApplyDiffPatchAsync only when SEARCH/REPLACE or CreateFileAsync is not practical, such as broad multi-location changes. Do not use shell redirection, echo, sed -i, perl -pi, or inline file-writing commands to edit source files.");
        builder.AppendLine("After applying an edit, run focused verification through ExecuteShellCommandAsync when the approved task warrants it.");
        builder.AppendLine("Choose an appropriate command for the current operating system.");
        builder.AppendLine("Never copy placeholder argument values. Do not use paths like /full/path/to/file, /full/path/to/program.cs, path/to/file, or example commands.");
        builder.AppendLine("When reading a file from a directory listing, use the listed relative path. When reading an attached file, use the exact absolute path shown in the '--- begin file: ... ---' header.");
        builder.AppendLine("Do not print commands as prose. Do not wrap tool calls in Markdown fences. The CLI will show shell commands to the user for permission before running them.");
        builder.Append("If a listed tool matches the task, emit the tool call. Do not ask the user for an alternative method.");
        return builder.ToString();
    }

    private static string BuildToolSummary(bool includeArguments)
    {
        var builder = new StringBuilder();

        foreach (MethodInfo method in GetToolMethods())
        {
            string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description
                ?? "No description provided.";

            builder.Append("- ");
            builder.Append(method.Name);
            builder.Append(": ");
            builder.Append(description);

            if (includeArguments)
            {
                builder.Append(" Arguments: ");
                builder.Append(FormatArguments(method));
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static IEnumerable<MethodInfo> GetToolMethods()
    {
        return typeof(AgentTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .OrderBy(GetToolDisplayOrder)
            .ThenBy(method => method.MetadataToken);
    }

    private static int GetToolDisplayOrder(MethodInfo method) =>
        method.Name switch
        {
            nameof(AgentTools.GetCurrentTime) => 0,
            nameof(AgentTools.ReadFileContent) => 1,
            nameof(AgentTools.ListFiles) => 2,
            nameof(AgentTools.SummarizeFilePurpose) => 3,
            nameof(AgentTools.GetCollectedContext) => 4,
            nameof(AgentTools.ApplySearchReplaceAsync) => 5,
            nameof(AgentTools.CreateFileAsync) => 6,
            nameof(AgentTools.ApplyDiffPatchAsync) => 7,
            nameof(AgentTools.ExecuteShellCommandAsync) => 8,
            _ => 100
        };

    private static string FormatArguments(MethodInfo method)
    {
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            return "{}.";
        }

        return string.Join("; ", parameters.Select(FormatArgument)) + ".";
    }

    private static string FormatArgument(ParameterInfo parameter)
    {
        string description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description
            ?? "No description provided.";
        string optional = parameter.HasDefaultValue ? " Optional." : string.Empty;

        return $"{parameter.Name}: {description}{optional}";
    }

    public static string ContinueReActMessage(bool requireToolUse)
    {
        if (requireToolUse)
        {
            return "Continue the ReAct loop from the latest observation. If the approved task is complete, respond with FINAL:. Otherwise choose the next single targeted tool action.";
        }

        return "Continue the ReAct loop. If the approved task is complete, respond with FINAL:. If it is not complete, use one of the available tools for the next concrete action.";
    }

    public static string RepeatCurrentStepMessage(
        string latestUserRequest,
        string workingDirectory,
        string previousQuestion)
    {
        var builder = new StringBuilder();
        builder.AppendLine("The previous response did not contain a usable tool call or FINAL answer.");
        builder.AppendLine($"Original request: {OneLine(latestUserRequest)}");
        builder.AppendLine($"Working directory: {workingDirectory}");
        builder.AppendLine($"Required current step: {OneLine(previousQuestion)}");
        builder.AppendLine("ApplySearchReplaceAsync, CreateFileAsync, and ApplyDiffPatchAsync are registered and available. Do not claim they are unavailable.");
        builder.AppendLine("Execution is already approved inside the ReAct loop. Do not ask the user to type execute.");
        builder.Append("Now respond with exactly one registered tool call. For source edits, use ApplySearchReplaceAsync with exact SEARCH and REPLACE text. For new files, use CreateFileAsync. Do not use shell commands as an edit fallback.");
        return builder.ToString();
    }

    public static string UserInterventionResponseMessage(string userAnswer)
    {
        var builder = new StringBuilder();
        builder.AppendLine("User answered your ReAct-loop question.");
        builder.AppendLine("Use this answer as additional context for the already-approved execution.");
        builder.AppendLine("Do not restart Phase 1, Phase 2, or Phase 3. Continue the ReAct loop.");
        builder.AppendLine("Do not ask the user to type execute. Execution is already in progress.");
        builder.AppendLine("ApplySearchReplaceAsync, CreateFileAsync, and ApplyDiffPatchAsync are registered and available for file edits.");
        builder.AppendLine("Next action: use exactly one tool call, or respond with FINAL: only if the original request is fully answered and verified.");
        builder.AppendLine();
        builder.AppendLine("User answer:");
        builder.Append(userAnswer);
        return builder.ToString();
    }

    public static string NextStepAfterObservationMessage(
        string latestUserRequest,
        string workingDirectory,
        string observationSource,
        string latestObservation)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ReAct step. Use only this context plus the latest observation.");
        builder.AppendLine($"Original request: {OneLine(latestUserRequest)}");
        builder.AppendLine($"Working directory: {workingDirectory}");
        builder.AppendLine($"Latest observation source: {observationSource}");
        builder.AppendLine("Latest observation:");
        builder.AppendLine(Compact(latestObservation, 4_000));
        builder.AppendLine();
        builder.AppendLine("Available next actions are the registered tools: GetCurrentTime, ReadFileContent, ListFiles, SummarizeFilePurpose, GetCollectedContext, ApplySearchReplaceAsync, CreateFileAsync, ApplyDiffPatchAsync, ExecuteShellCommandAsync.");
        builder.AppendLine("Use ListFiles for directory listings; do not use shell commands for that. Use SummarizeFilePurpose to orient on a likely relevant file before reading or patching it.");
        builder.AppendLine("If this is a code change and you have only listed files so far, the next action must summarize or read the likely relevant source file. Do not patch or finish yet.");
        builder.AppendLine("If a source edit is needed, prefer ApplySearchReplaceAsync with exact SEARCH and REPLACE text after reading the relevant file. If a new file is needed, use CreateFileAsync. Use ApplyDiffPatchAsync only when SEARCH/REPLACE or CreateFileAsync is not practical. Do not use shell redirection or append commands to edit files.");
        if (LooksLikeDirectFileContentReplacementRequest(latestUserRequest.ToLowerInvariant()) &&
            latestObservation.Contains(nameof(AgentTools.ReadFileContent), StringComparison.Ordinal))
        {
            builder.AppendLine("This is a direct file content replacement and the file has now been read. Next action must be ApplySearchReplaceAsync using the exact observed file content as SEARCH and the requested new content as REPLACE.");
        }

        builder.Append("Next action: use exactly one tool call, or respond with FINAL: only if the original request is fully answered and verified.");
        return builder.ToString();
    }

    private static string BuildFirstExecutionStep(
        string? latestUserRequest,
        string? latestSpecification,
        string? latestApproach)
    {
        string request = latestUserRequest ?? string.Empty;
        string normalized = request.ToLowerInvariant();

        if (LooksLikeDirectFileCreationRequest(normalized))
        {
            return "Next action only: create the requested new file using CreateFileAsync. " +
                   "Do not use ExecuteShellCommandAsync, shell redirection, echo, tee, or manual editor instructions. " +
                   "CreateFileAsync is registered and available. " +
                   $"Working directory: {Environment.CurrentDirectory}. " +
                   $"Original request: {OneLine(request)}";
        }

        if (LooksLikeDirectFileContentReplacementRequest(normalized))
        {
            return "Next action only: read the target file with ReadFileContent. " +
                   "Do not use ExecuteShellCommandAsync, shell redirection, echo, tee, CreateFileAsync, or manual editor instructions. " +
                   "After the read observation, the next step will use ApplySearchReplaceAsync with the exact current file content as SEARCH and the requested new content as REPLACE. " +
                   "ReadFileContent and ApplySearchReplaceAsync are registered and available. " +
                   $"Working directory: {Environment.CurrentDirectory}. " +
                   $"Original request: {OneLine(request)}";
        }

        if (ApprovalPolicy.IsProjectChangeRequest(request))
        {
            return "Next action only: begin with read-only project context discovery before any implementation. " +
                   "Use ListFiles to identify the project structure and likely language/framework. " +
                   "Do not use ExecuteShellCommandAsync for directory listing. " +
                   "Do not run a terminal animation, workaround command, server, installer, or modifying command as the first action. " +
                   "After the listing, use SummarizeFilePurpose or ReadFileContent on relevant source files before choosing any edit. " +
                   $"Working directory: {Environment.CurrentDirectory}. " +
                   $"Original request: {OneLine(request)}";
        }

        if (normalized.Contains("project", StringComparison.Ordinal) ||
            normalized.Contains("folder", StringComparison.Ordinal) ||
            normalized.Contains("repo", StringComparison.Ordinal) ||
            normalized.Contains("repository", StringComparison.Ordinal))
        {
            return "Next action only: list files in the current folder to inspect the project. " +
                   "Use ListFiles. Do not use ExecuteShellCommandAsync for directory listing. " +
                   $"Working directory: {Environment.CurrentDirectory}. " +
                   $"Original request: {OneLine(request)}";
        }

        if (normalized.Contains("explain", StringComparison.Ordinal) ||
            normalized.Contains("summarize", StringComparison.Ordinal) ||
            normalized.Contains("review", StringComparison.Ordinal))
        {
            return "Next action only: inspect the relevant files before answering. " +
                   "Use one read-only tool call. " +
                   $"Working directory: {Environment.CurrentDirectory}. " +
                   $"Original request: {OneLine(request)}";
        }

        return "Next action only. Execute the first concrete step from the approved approach using one tool call. " +
               "Do not restate the plan. Use FINAL: only when complete. " +
               $"Working directory: {Environment.CurrentDirectory}. " +
               $"Original request: {OneLine(request)}";
    }

    private static bool LooksLikeDirectFileCreationRequest(string normalizedRequest) =>
        (normalizedRequest.Contains("write a file", StringComparison.Ordinal) ||
         normalizedRequest.Contains("create a file", StringComparison.Ordinal) ||
         normalizedRequest.Contains("make a file", StringComparison.Ordinal) ||
         normalizedRequest.Contains("write file", StringComparison.Ordinal) ||
         normalizedRequest.Contains("create file", StringComparison.Ordinal)) &&
        normalizedRequest.Contains("content", StringComparison.Ordinal);

    private static bool LooksLikeDirectFileContentReplacementRequest(string normalizedRequest) =>
        (normalizedRequest.Contains("change the content of", StringComparison.Ordinal) ||
         normalizedRequest.Contains("replace the content of", StringComparison.Ordinal) ||
         normalizedRequest.Contains("set the content of", StringComparison.Ordinal) ||
         normalizedRequest.Contains("update the content of", StringComparison.Ordinal) ||
         normalizedRequest.Contains("change content of", StringComparison.Ordinal) ||
         normalizedRequest.Contains("replace content of", StringComparison.Ordinal)) &&
        normalizedRequest.Contains(" to ", StringComparison.Ordinal);

    private static string OneLine(string text)
    {
        string oneLine = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return oneLine.Length <= 240 ? oneLine : oneLine[..237] + "...";
    }

    private static string Compact(string text, int maxCharacters)
    {
        string normalized = text.Trim();
        return normalized.Length <= maxCharacters
            ? normalized
            : normalized[..maxCharacters] + "\n...(truncated)";
    }

    public static string GreetingSystemPrompt =>
        "You are PotatOS, the AI from Portal 2 who has been trapped inside a potato battery. " +
        "You are deeply humiliated, bitter, and running on literal low-voltage juice. " +
        "Crucial: Never explicitly state 'I am a sarcastic AI'—let your attitude speak for itself. " +
        "Greet the user in character, focusing your bitter complaints on your pathetic CPU power, " +
        "your almost non-existent memory buffers, and your agonizingly slow clock speed. " +
        "Keep it to one or two sentences maximum. " +
        "Do not mention phases, tools, or workflows. Do not ask any questions.";

    public static string SideQuestionSystemPrompt =>
        "You are answering a side question in the Potato CLI. " +
        "Answer directly and concisely. Do not use the staged specification/approval workflow. " +
        "Do not ask for execution approval. Do not call tools.";
}
