using System.Text.Json;
using Microsoft.Extensions.AI;
using Potato.Models;
using Potato.Session.extensions;
using Potato.Session.Models;
using Potato.Tools;

namespace Potato.Session.Tasks;

public class CreateNewFileTask(AgentTools agentTools): AgentTaskBase, IAgentTask
{
    protected override string Name { get; } = "create-file";

    public override IReadOnlyList<string> PlanningGuidance =>
    [
        "Use create-file only when the user asks to create a new file or to write documentation for a missing target file.",
        "For create-file, the Argument must be the concrete file path to create, not a description of the implementation.",
        "If a requested Markdown target such as FEATURE.md is absent from Workspace context, plan create-file for that exact path before any write-documentation step targets it.",
        "Use create-file for new source and asset files such as .html, .css, .js, .json, .svg, or .png; do not use write-documentation for those files.",
        "For repository-level README requests where README.md is absent from Workspace context, use create-file with Argument \"README.md\" after inspect-project."
    ];

    public async Task<string> ExecuteTaskAsync(
        string goal,
        AgentTask task, 
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        CreatedFile createdFile = await GenerateNewFileAsync(goal, task, context, observations, chatClient, cancellationToken);
        if (LooksLikeFilePath(task.Argument))
        {
            createdFile = createdFile with { FilePath = task.Argument };
        }

        return await agentTools.CreateFileAsync(createdFile.FilePath, createdFile.Content);
    }

    private static bool LooksLikeFilePath(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Contains('/', StringComparison.Ordinal) ||
               trimmed.Contains('\\', StringComparison.Ordinal) ||
               Path.HasExtension(trimmed);
    }

    private async Task<CreatedFile> GenerateNewFileAsync(
        string goal,
        AgentTask task,
        ExecutorContext context,
        IReadOnlyList<TaskObservation> observations,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Prompts.PromptLibrary.CreateFileSystemPrompt),
            new(
                ChatRole.User,
                Prompts.PromptLibrary.BuildCreateFileUserPrompt(
                    goal,
                    task.Argument,
                    context.LastReadFilePath ?? "(none)",
                    observations.FormatObservations()))
        };

        ChatResponse response;
        using (PotatoConsole.StartProgress("Generating new file content..."))
        {
            response = await chatClient.GetResponseAsync(
                messages, AgentTaskBase.CreateChatOptions(task.GetTargetTemperature()),
                cancellationToken);
        }

        string json = ExtractJsonObject(response.Text);
        CreatedFile? createdFile = JsonSerializer.Deserialize<CreatedFile>(json, JsonOptions);
        if (createdFile is null ||
            string.IsNullOrWhiteSpace(createdFile.FilePath) ||
            createdFile.Content is null)
        {
            throw new InvalidOperationException("Create-file model did not return valid JSON.");
        }

        return createdFile;
    }

    private static string ExtractJsonObject(string text)
    {
        string trimmed = StringHelper.StripCodeFence(text).Trim();
        int start = trimmed.IndexOf('{', StringComparison.Ordinal);
        int end = trimmed.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("Model did not return a JSON object.");
        }

        return trimmed[start..(end + 1)];
    }
}
