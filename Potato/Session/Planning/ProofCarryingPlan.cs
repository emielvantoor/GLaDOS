using System.Text;

namespace Potato.Session.Planning;

/// <summary>
/// A user-reviewable contract for an agent run. It intentionally separates what
/// the model proposes from the evidence later collected by the execution loop.
/// </summary>
internal sealed record ProofCarryingPlan(string Goal, IReadOnlyList<ProofPlanStep> Steps)
{
    public string FormatForReview()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Goal: {Goal}");

        for (int index = 0; index < Steps.Count; index++)
        {
            ProofPlanStep step = Steps[index];
            builder.AppendLine();
            builder.AppendLine($"{index + 1}. {step.Title}");
            builder.AppendLine($"   Action: {step.Action}");
            builder.AppendLine($"   Evidence needed: {step.Evidence}");
            builder.AppendLine($"   Expected result: {step.ExpectedResult}");
            builder.AppendLine($"   Verification: {step.Verification}");
            builder.AppendLine($"   Rollback: {step.Rollback}");
        }

        return builder.ToString().TrimEnd();
    }

    public string FormatForExecution() => FormatForReview();
}

internal sealed record ProofPlanStep(
    string Title,
    string Action,
    string Evidence,
    string ExpectedResult,
    string Verification,
    string Rollback);

/// <summary>Observed evidence attached to a proof-carrying plan at runtime.</summary>
internal sealed class ProofExecutionLedger
{
    private readonly List<string> observations = [];
    private bool changedFiles;
    private bool verificationRan;

    public void Record(string source, string observation)
    {
        string outcome = FirstLine(observation);
        observations.Add($"{source}: {outcome}");

        if (source is "ApplySearchReplaceAsync" or "ApplyFimEditAsync" or "CreateFileAsync" or "ApplyDiffPatchAsync")
        {
            changedFiles |= !outcome.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) &&
                            !outcome.StartsWith("Rejected", StringComparison.OrdinalIgnoreCase) &&
                            !outcome.Contains(" denied", StringComparison.OrdinalIgnoreCase);
        }

        if (source == "ExecuteShellCommandAsync")
        {
            verificationRan |= !outcome.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) &&
                               !outcome.StartsWith("Rejected", StringComparison.OrdinalIgnoreCase) &&
                               !outcome.Contains("denied", StringComparison.OrdinalIgnoreCase);
        }
    }

    public string BuildCompletionEvidence()
    {
        string verification = changedFiles
            ? verificationRan
                ? "Verification evidence was collected after a file change."
                : "A file change was made, but no shell verification evidence was collected."
            : "No file change was made during this run.";

        string observed = observations.Count == 0
            ? "No tool observations were collected."
            : string.Join(Environment.NewLine, observations.TakeLast(8).Select(item => $"- {item}"));

        return $"""
            Proof-carrying execution record
            {verification}
            Recent observed evidence:
            {observed}
            """;
    }

    private static string FirstLine(string text)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        int lineEnd = normalized.IndexOf('\n');
        return lineEnd < 0 ? normalized : normalized[..lineEnd];
    }
}
