namespace Potato.Session.Models;

public sealed record TaskObservation(int Step, string Action, string Argument, string Result);

public sealed class ExecutorContext
{
    public string? LastReadFilePath { get; set; }
    public string? LastReadFileContent { get; set; }
}

public sealed record ExecutionResult(
    bool Success,
    IReadOnlyList<TaskObservation> Observations,
    string? ErrorMessage)
{
    public static ExecutionResult Succeeded(IReadOnlyList<TaskObservation> observations) =>
        new(true, observations, null);

    public static ExecutionResult Failed(IReadOnlyList<TaskObservation> observations, string errorMessage) =>
        new(false, observations, errorMessage);
}
