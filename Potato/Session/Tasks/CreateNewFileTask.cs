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
        "Use create-file only when the user asks to create a new file."
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
        return await agentTools.CreateFileAsync(createdFile.FilePath, createdFile.Content);
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
                "Return a JSON object only.\n\n" +
                $"Goal:\n{goal}\n\n" +
                $"Create task:\n{task.Argument}\n\n" +
                $"Last read file: {context.LastReadFilePath ?? "(none)"}\n\n" +
                "Prior observations:\n" +
                observations.FormatObservations())
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
