namespace Potato.Models;

/// <summary>
/// Categorizes tool results to enable context-aware, per-type truncation strategies.
/// Different result types benefit from different truncation approaches.
/// </summary>
public enum ToolResultType
{
    /// <summary>
    /// Generic/unknown result type. Uses basic length-based truncation.
    /// </summary>
    Generic = 0,

    /// <summary>
    /// File content read from disk. Truncation strategy: keep first N lines + last M lines to preserve structure.
    /// </summary>
    FileContent = 1,

    /// <summary>
    /// Shell/PowerShell command output. Truncation strategy: keep status + first N lines + error section + last M lines.
    /// </summary>
    ShellOutput = 2,

    /// <summary>
    /// Search results (file search, content search, ProjectMap search). Truncation strategy: keep top N results + total count.
    /// </summary>
    SearchResults = 3,

    /// <summary>
    /// AI-generated summary or analysis. No truncation; keep full or use structured summary format.
    /// </summary>
    Summary = 4,

    /// <summary>
    /// Directory listing or file list. Truncation strategy: keep first N entries + ellipsis + count total.
    /// </summary>
    DirectoryListing = 5,

    /// <summary>
    /// Patch or diff output. Truncation strategy: keep hunks structure + context lines around changes.
    /// </summary>
    PatchDiff = 6,

    /// <summary>
    /// Error or exception message. Never truncate; keep full for debugging.
    /// </summary>
    Error = 7,

    /// <summary>
    /// Current time or system information. Keep full, these are usually small.
    /// </summary>
    SystemInfo = 8
}
