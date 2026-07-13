using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Potato.Session.Tasks;

namespace Potato.Session;

public sealed class PlanningArtifactGenerator
{
    private const int MaxDraftPlanAttempts = 5;

    public async Task<string> GeneratePlanningSpecAsync(
        string goal,
        string workspaceFileIndex,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.PlanningSpecSystemPrompt),
            new(ChatRole.User, Prompts.PromptLibrary.BuildPlanningSpecUserPrompt(goal, workspaceFileIndex))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress("Generating implementation spec..."))
        {
            response = await chatClient.GetResponseAsync(
                messages,
                AgentTaskBase.CreateJsonChatOptions(0.0),
                cancellationToken);
        }

        return ResolveSpecPathReferences(PlanningJsonExtractor.ExtractJsonObject(response.Text), workspaceFileIndex);
    }

    public async Task<string> GenerateApprovedDraftPlanAsync(
        string goal,
        string planningSpec,
        string workspaceContext,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var draftFeedback = new List<string>();
        string latestDraftPlan = string.Empty;
        for (int attempt = 1; attempt <= MaxDraftPlanAttempts; attempt++)
        {
            latestDraftPlan = await GenerateDraftPlanAsync(
                goal,
                planningSpec,
                workspaceContext,
                FormatDraftFeedback(draftFeedback),
                chatClient,
                cancellationToken);

            PlanCompletenessReview review = await ReviewDraftPlanAsync(
                planningSpec,
                latestDraftPlan,
                chatClient,
                cancellationToken);
            if (review.IsComplete)
            {
                return latestDraftPlan;
            }

            string feedback = string.IsNullOrWhiteSpace(review.Feedback)
                ? "Draft plan does not satisfy the derived implementation spec."
                : review.Feedback.Trim();
            draftFeedback.Add(feedback);
            PotatoConsole.WriteStatus($"Draft plan review failed: {feedback}");
        }

        throw new InvalidOperationException(
            $"Planner could not produce a complete draft plan after {MaxDraftPlanAttempts} attempts: {draftFeedback.LastOrDefault() ?? latestDraftPlan}");
    }

    private static async Task<string> GenerateDraftPlanAsync(
        string goal,
        string planningSpec,
        string workspaceContext,
        string draftFeedback,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.DraftPlanSystemPrompt),
            new(
                ChatRole.User,
                Prompts.PromptLibrary.BuildDraftPlanUserPrompt(
                    goal,
                    planningSpec,
                    workspaceContext,
                    draftFeedback))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress("Drafting implementation plan..."))
        {
            response = await chatClient.GetResponseAsync(
                messages,
                AgentTaskBase.CreateJsonChatOptions(0.0),
                cancellationToken);
        }

        return PlanningJsonExtractor.ExtractJsonArray(response.Text);
    }

    private static string ResolveSpecPathReferences(string planningSpec, string workspaceContext)
    {
        IReadOnlySet<string> indexedPaths = PlanningPathUtilities.ExtractIndexedPaths(workspaceContext);
        string resolvedSpec = planningSpec;
        foreach (string fileName in PlanningPathUtilities.ExtractLikelyFileNames(planningSpec).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (PlanningPathUtilities.TryResolveUniqueIndexedBasename(fileName, indexedPaths, out string resolvedPath))
            {
                resolvedSpec = Regex.Replace(
                    resolvedSpec,
                    $@"(?<![\w./-]){Regex.Escape(fileName)}(?![\w./-])",
                    resolvedPath,
                    RegexOptions.IgnoreCase);
            }
        }

        return resolvedSpec;
    }

    private static async Task<PlanCompletenessReview> ReviewDraftPlanAsync(
        string planningSpec,
        string draftPlan,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.DraftPlanReviewSystemPrompt),
            new(ChatRole.User, Prompts.PromptLibrary.BuildDraftPlanReviewUserPrompt(planningSpec, draftPlan))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress("Reviewing draft plan..."))
        {
            response = await chatClient.GetResponseAsync(
                messages,
                AgentTaskBase.CreateJsonChatOptions(0.0),
                cancellationToken);
        }

        return ParsePlanCompletenessReview(response.Text, "Draft plan reviewer returned no review.");
    }

    private static string FormatDraftFeedback(IReadOnlyList<string> draftFeedback)
    {
        if (draftFeedback.Count == 0)
        {
            return "(none)";
        }

        var builder = new StringBuilder();
        for (int index = 0; index < draftFeedback.Count; index++)
        {
            builder.AppendLine($"{index + 1}. {draftFeedback[index]}");
        }

        return builder.ToString();
    }

    private static PlanCompletenessReview ParsePlanCompletenessReview(string text, string nullReviewMessage)
    {
        string json = PlanningJsonExtractor.ExtractJsonObject(text);
        PlanCompletenessReview? review = JsonSerializer.Deserialize<PlanCompletenessReview>(
            json,
            AgentTaskBase.JsonOptions);
        if (review is null)
        {
            throw new InvalidOperationException(nullReviewMessage);
        }

        return review;
    }
}
