using Microsoft.Extensions.AI;
using Potato.Prompts;
using Potato.Session.Planning;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Potato.Session;

public class PlanningService(ProjectMapBuilder projectMapBuilder)
{
    public string BuildDirectExecutionGuidance(string currentDirectory) =>
        PromptLibrary.BuildDirectExecutionGuidance(currentDirectory);

    public Task<string> BuildProjectMapHeaderAsync(string targetDirectory, CancellationToken cancellationToken) =>
        projectMapBuilder.BuildProjectMapHeaderAsync(targetDirectory, cancellationToken);

    public Task<string> SearchProjectMapAsync(
        string targetDirectory,
        string query,
        int maxResults,
        IChatClient? chatClient,
        CancellationToken cancellationToken) =>
        projectMapBuilder.SearchProjectMapAsync(targetDirectory, query, maxResults, chatClient, cancellationToken);

    internal async Task<ProofCarryingPlan> CreateProofCarryingPlanAsync(
        string goal,
        string currentDirectory,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, PromptLibrary.BuildProofPlanSystemPrompt()),
                new(ChatRole.User, PromptLibrary.BuildProofPlanUserPrompt(goal, currentDirectory))
            };
            ChatResponse response = await chatClient.GetResponseAsync(
                messages,
                new ChatOptions { Temperature = 0.0f, MaxOutputTokens = 1600 },
                cancellationToken);
            return ParseProofPlan(response.Text, goal);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return CreateFallbackPlan(goal);
        }
    }

    private static ProofCarryingPlan ParseProofPlan(string? responseText, string goal)
    {
        try
        {
            JsonObject? root = ExtractJsonObject(responseText);
            JsonArray? rawSteps = root?["steps"] as JsonArray;
            if (rawSteps is null || rawSteps.Count == 0)
            {
                return CreateFallbackPlan(goal);
            }

            ProofPlanStep[] steps = rawSteps
                .OfType<JsonObject>()
                .Select(step => new ProofPlanStep(
                    Read(step, "title", "Inspect and implement"),
                    Read(step, "action", "Use one appropriate Potato tool"),
                    Read(step, "evidence", "Collect relevant repository evidence before changing files"),
                    Read(step, "expectedResult", "The requested outcome is observable"),
                    Read(step, "verification", "Run the smallest relevant existing validation"),
                    Read(step, "rollback", "Revert the approved diff checkpoint")))
                .Take(6)
                .ToArray();

            return steps.Length == 0
                ? CreateFallbackPlan(goal)
                : new ProofCarryingPlan(Read(root!, "goal", goal), steps);
        }
        catch
        {
            return CreateFallbackPlan(goal);
        }
    }

    private static JsonObject? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        return start < 0 || end <= start
            ? null
            : JsonNode.Parse(text[start..(end + 1)]) as JsonObject;
    }

    private static string Read(JsonObject source, string name, string fallback) =>
        source[name]?.GetValue<string>()?.Trim() is { Length: > 0 } value ? value : fallback;

    private static ProofCarryingPlan CreateFallbackPlan(string goal) => new(
        goal,
        [
            new ProofPlanStep(
                "Inspect relevant context",
                "Use read-only repository tools to locate and inspect the relevant files.",
                "File paths, source excerpts, or test output that directly relate to the goal.",
                "The target and its current behavior are confirmed before any change.",
                "The collected observation identifies the exact target.",
                "No files are changed in this step."),
            new ProofPlanStep(
                "Make the smallest justified change",
                "Apply one focused edit after the target content has been observed.",
                "The approved edit and its resulting tool observation.",
                "The requested behavior or artifact is present at the intended path.",
                "Run the smallest relevant existing check, or record the static check if none exists.",
                "Revert the approved diff or restore the previous file content.")
        ]);
}
