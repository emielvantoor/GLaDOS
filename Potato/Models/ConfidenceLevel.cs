namespace Potato.Models;

/// <summary>
/// Confidence level for whether a summary is sufficient or full content retrieval is needed
/// </summary>
public enum ConfidenceLevel
{
    /// <summary>
    /// Confidence level unknown or not specified (default to blocking)
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Low confidence - summary may be insufficient, should retrieve full content
    /// </summary>
    Low = 1,

    /// <summary>
    /// Medium confidence - summary covers basics but full content might be needed for details
    /// </summary>
    Medium = 2,

    /// <summary>
    /// High confidence - summary is sufficient for the goal, full content not required
    /// </summary>
    High = 3
}
