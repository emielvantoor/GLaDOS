internal static class ApprovalPolicy
{
    public static bool IsUserApproval(string input)
    {
        string normalized = input.Trim().ToLowerInvariant();
        string[] approvalWords = ["y", "yes", "approved", "approve", "go", "fine", "good", "looks good", "ok", "okay", "correct"];
        return Array.Exists(approvalWords, word => normalized == word || normalized.StartsWith(word + " "));
    }

    public static bool IsUserExecutionApproval(string input)
    {
        string normalized = input.Trim().ToLowerInvariant();
        string[] executeWords = ["execute", "run", "do it", "continue", "proceed", "go"];
        return Array.Exists(executeWords, word => normalized == word || normalized.StartsWith(word + " "));
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
}
