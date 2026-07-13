namespace Potato.Session;

internal sealed class PlanCompletenessReview
{
    public bool IsComplete { get; init; }

    public string Feedback { get; init; } = string.Empty;
}
