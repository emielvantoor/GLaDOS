internal static class ApprovalPolicy
{
    public static bool IsUserApproval(string input)
    {
        string normalized = NormalizeApprovalInput(input);
        string[] approvalWords = ["y", "yes", "approved", "approve", "go", "fine", "good", "looks good", "ok", "okay", "correct"];
        return Array.Exists(approvalWords, word => normalized == word);
    }

    public static bool IsUserExecutionApproval(string input)
    {
        string normalized = NormalizeApprovalInput(input);
        string[] executeWords = ["y", "yes", "approved", "approve", "ok", "okay", "execute", "run", "do it", "continue", "proceed", "go"];
        return Array.Exists(executeWords, word => normalized == word);
    }

    public static bool RequiresExplicitExecutionApproval(string? latestSpecification, string? latestApproach)
    {
        string text = $"{latestSpecification}\n{latestApproach}".ToLowerInvariant();
        string[] riskySignals =
        [
            "delete", "remove", "rm ", "rmdir", "del ",
            "write", "modify", "edit", "overwrite", "replace", "rename", "move ",
            "create", "mkdir", "touch", "install", "uninstall", "upgrade", "update",
            "download", "curl", "wget", "chmod", "chown", "sudo",
            "kill", "stop service", "restart", "format", "mount", "umount",
            "multiple steps", "several steps", "then run", "after that"
        ];

        return riskySignals.Any(text.Contains);
    }

    public static bool IsReadOnlyInspectionRequest(string? userRequest)
    {
        string text = userRequest?.ToLowerInvariant() ?? string.Empty;
        string[] readOnlySignals =
        [
            "explain", "summarize", "describe", "what is", "what does",
            "review", "inspect", "analyze", "list", "show", "find"
        ];

        string[] writeSignals =
        [
            "change", "edit", "modify", "fix", "implement", "add", "remove",
            "delete", "create", "install", "update", "rename", "move"
        ];

        return readOnlySignals.Any(text.Contains) && !writeSignals.Any(text.Contains);
    }

    public static bool ShouldAutoExecuteAfterApproach(
        string? latestUserRequest,
        string? latestSpecification,
        string? latestApproach)
    {
        if (IsReadOnlyInspectionRequest(latestUserRequest))
        {
            return true;
        }

        return !RequiresExplicitExecutionApproval(latestSpecification, latestApproach);
    }

    private static string NormalizeApprovalInput(string input)
    {
        return input
            .Trim()
            .Trim('.', '!', '?')
            .ToLowerInvariant();
    }
}
