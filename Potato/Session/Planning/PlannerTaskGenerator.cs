using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Session.Tasks;

namespace Potato.Session;

public sealed class PlannerTaskGenerator(
    IEnumerable<IAgentTask> agentTasks,
    PlanTaskNormalizer taskNormalizer)
{
    private const int MaxPlannerRepairAttempts = 3;

    public async Task<List<AgentTask>> GenerateTaskListAsync(
        string goal,
        IReadOnlyList<TaskObservation> observations,
        string workspaceContext,
        string workspacePlanningContext,
        string planningSpec,
        string draftPlan,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> supportedActions = GetSupportedActions();
        string executionObservations = observations.FormatObservations();
        Exception? lastPlanningError = null;
        var plannerRepairMessages = new List<string>();

        for (int attempt = 1; attempt <= MaxPlannerRepairAttempts; attempt++)
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, Prompts.PromptLibrary.PlannerSystemPrompt),
                new(ChatRole.User, Prompts.PromptLibrary.BuildPlannerUserPrompt(
                    goal,
                    workspacePlanningContext,
                    planningSpec,
                    draftPlan,
                    supportedActions,
                    executionObservations))
            };

            ChatResponse response;
            using (PotatoConsole.StartProgress(FormatPlanningProgress(attempt)))
            {
                response = await chatClient.GetResponseAsync(messages, AgentTaskBase.CreateJsonChatOptions(0.0),
                    cancellationToken);
            }

            try
            {
                List<AgentTask> tasks = ExtractPlannerTasks(response, supportedActions);
                if (tasks.Count == 0)
                {
                    throw new InvalidOperationException("Planner returned no tasks.");
                }

                return taskNormalizer.Normalize(tasks, goal, workspaceContext, observations);
            }
            catch (InvalidOperationException ex)
            {
                lastPlanningError = ex;
                plannerRepairMessages.Add(ex.Message);
                if (attempt == MaxPlannerRepairAttempts)
                {
                    break;
                }

                PotatoConsole.WriteStatus($"Planner response repair needed: {ex.Message}");
                executionObservations = BuildPlannerRetryObservations(observations, plannerRepairMessages);
            }
        }

        throw new InvalidOperationException(
            $"Planner could not produce a valid task list after {MaxPlannerRepairAttempts} attempts: {lastPlanningError?.Message}");
    }

    private static string FormatPlanningProgress(int attempt) =>
        attempt == 1
            ? "Planning deterministic task list..."
            : $"Repairing planner task list ({attempt}/{MaxPlannerRepairAttempts})...";

    private static string BuildPlannerRetryObservations(
        IReadOnlyList<TaskObservation> observations,
        IReadOnlyList<string> repairMessages)
    {
        var builder = new StringBuilder();
        string formattedObservations = observations.FormatObservations();
        if (!string.Equals(formattedObservations, "(none)", StringComparison.Ordinal))
        {
            builder.AppendLine(formattedObservations.TrimEnd());
            builder.AppendLine();
        }

        builder.AppendLine("Planner response repair feedback from previous attempts:");
        foreach ((string repairMessage, int index) in repairMessages.Select((message, index) => (message, index)))
        {
            builder.AppendLine($"{index + 1}. {repairMessage}");
        }

        builder.AppendLine("Return a corrected JSON array that completes the user request and addresses all repair feedback above.");
        return builder.ToString();
    }

    private IReadOnlyList<string> GetSupportedActions() =>
        agentTasks
            .Select(task => task.ActionName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(action => action, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static List<AgentTask> ExtractPlannerTasks(ChatResponse response, IReadOnlyList<string> supportedActions)
    {
        if (!string.IsNullOrWhiteSpace(response.Text))
        {
            string json = PlanningJsonExtractor.ExtractJsonArray(response.Text);
            return JsonSerializer.Deserialize<List<AgentTask>>(json, AgentTaskBase.JsonOptions) ?? [];
        }

        FunctionCallContent[] functionCalls = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .ToArray();
        if (functionCalls.Length == 0)
        {
            throw new InvalidOperationException("Planner did not return a JSON array.");
        }

        var supportedActionSet = new HashSet<string>(supportedActions, StringComparer.OrdinalIgnoreCase);
        var tasks = new List<AgentTask>();
        foreach ((FunctionCallContent functionCall, int index) in functionCalls.Select((functionCall, index) => (functionCall, index)))
        {
            string action = StringHelper.NormalizeAction(functionCall.Name);
            if (!supportedActionSet.Contains(action))
            {
                throw new InvalidOperationException($"Planner returned unsupported function call: {functionCall.Name}.");
            }

            tasks.Add(new AgentTask
            {
                Step = index + 1,
                Action = action,
                Argument = FormatFunctionCallArgument(action, functionCall.Arguments ?? new Dictionary<string, object?>()),
                Reason = $"Continue planning with the supported {action} task requested by the planner."
            });
        }

        return tasks;
    }

    private static string FormatFunctionCallArgument(string action, IDictionary<string, object?> arguments)
    {
        if (StringHelper.NormalizeAction(action) == "search-project-map")
        {
            string query = GetStringArgument(arguments, "query") ??
                           GetStringArgument(arguments, "keywords") ??
                           GetStringArgument(arguments, "searchTerms") ??
                           string.Empty;
            int maxResults = GetIntArgument(arguments, "maxResults") ??
                             GetIntArgument(arguments, "max_results") ??
                             12;

            return $"Query: {query}{Environment.NewLine}Max results: {maxResults}";
        }

        return JsonSerializer.Serialize(arguments, AgentTaskBase.JsonOptions);
    }

    private static string? GetStringArgument(IDictionary<string, object?> arguments, string key)
    {
        if (!TryGetArgument(arguments, key, out object? value) || value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        }

        return value.ToString();
    }

    private static int? GetIntArgument(IDictionary<string, object?> arguments, string key)
    {
        if (!TryGetArgument(arguments, key, out object? value) || value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int jsonValue)
                ? jsonValue
                : int.TryParse(element.ToString(), out int parsedJsonValue)
                    ? parsedJsonValue
                    : null;
        }

        return value is int intValue
            ? intValue
            : int.TryParse(value.ToString(), out int parsedValue)
                ? parsedValue
                : null;
    }

    private static bool TryGetArgument(IDictionary<string, object?> arguments, string key, out object? value)
    {
        foreach (KeyValuePair<string, object?> argument in arguments)
        {
            if (string.Equals(argument.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = argument.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
