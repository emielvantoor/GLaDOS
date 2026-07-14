using System.Runtime.CompilerServices;
using GLaDOS.Core.Interfaces;
using GLaDOS.Core.Models;
using GLaDOS.Core.Protocols;
using GLaDOS.Core.Routing;
using GLaDOS.Core.ToolAdapters;
using Microsoft.Extensions.Logging;

namespace GLaDOS.Core.Agents;

public class GLaDOSAgent
{
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolRouter _toolRouter;
    private readonly ToolCallAdapterPipeline _toolCallAdapterPipeline;
    private readonly QwenProtocol _defaultProtocol;
    private readonly IReadOnlyList<IAgentProtocol> _protocols;
    private readonly ILogger<GLaDOSAgent> _logger;

    public GLaDOSAgent(
        ToolRegistry toolRegistry,
        ToolRouter toolRouter,
        ToolCallAdapterPipeline toolCallAdapterPipeline,
        QwenProtocol defaultProtocol,
        IEnumerable<IAgentProtocol> protocols,
        ILogger<GLaDOSAgent> logger)
    {
        _toolRegistry = toolRegistry;
        _toolRouter = toolRouter;
        _toolCallAdapterPipeline = toolCallAdapterPipeline;
        _defaultProtocol = defaultProtocol;
        _protocols = protocols.ToList();
        _logger = logger;
    }

    public async IAsyncEnumerable<string> RunAsync(
        LanguageModel model,
        List<AgentMessage> chatHistory,
        ChatOptions options,
        List<AgentToolDefinition>? externalTools = null, 
        string? protocolName = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var protocol = ResolveProtocol(protocolName);
        _logger.LogInformation("Starting GLaDOSAgent.RunAsync with {Protocol} protocol", protocol.Name);

        var toolDefinitions = _toolRegistry.GetDefinitions().ToList();

        if (externalTools != null)
        {
            toolDefinitions.AddRange(externalTools);
        }

        int maxIterations = 5;
        int currentIteration = 0;
        bool keepRunning = true;
        AgentToolResult? lastInternalToolResult = null;
        bool yieldedAssistantText = false;
        bool delegatedExternalTool = false;

        while (keepRunning && currentIteration < maxIterations)
        {
            currentIteration++;

            var promptHistory = ApplyContextSize(protocol, chatHistory, toolDefinitions, options.ContextSize);
            var prompt = protocol.BuildPrompt(promptHistory, toolDefinitions);
            var response = await model.GenerateResponseAsync(prompt, options, cancellationToken);
            var toolCall = protocol.ParseResponse(response).FirstOrDefault();

            if (toolCall == null)
            {
                var text = CleanAssistantText(protocol, response);
                if (!string.IsNullOrEmpty(text))
                {
                    yieldedAssistantText = true;
                    yield return text;
                }

                keepRunning = false;
                continue;
            }

            _toolCallAdapterPipeline.Adapt(
                toolCall,
                new ToolCallAdapterContext(toolDefinitions, chatHistory));

            var toolResult = await _toolRouter.RouteAsync(toolCall);
            var toolArgs = toolCall.Arguments?.ToJsonString() ?? "{}";

            if (toolResult.IsExternal)
            {
                yield return $"\n[Systeem: Delegating external tool '{toolCall.ToolName}' to client...]\n";
                yield return $"__TOOL_CALL__:{toolCall.ToolName}|{toolArgs}";
                delegatedExternalTool = true;
                break;
            }

            yield return $"\n[Systeem: Executing internal tool '{toolCall.ToolName}' with arguments: {toolArgs}...]\n";

            chatHistory.Add(new AgentMessage(AgentRole.Assistant, string.Empty, toolCall.ToolName, toolArgs));
            chatHistory.Add(new AgentMessage(AgentRole.Tool, toolResult.Output, toolCall.ToolName, toolCall.RawCall));
            lastInternalToolResult = toolResult;
        }

        if (!yieldedAssistantText && !delegatedExternalTool && lastInternalToolResult != null)
        {
            yield return lastInternalToolResult.Output;
        }

        _logger.LogInformation("Finished GLaDOSAgent.RunAsync");
    }

    private IAgentProtocol ResolveProtocol(string? protocolName)
    {
        if (string.IsNullOrWhiteSpace(protocolName))
        {
            return _defaultProtocol;
        }

        return _protocols.FirstOrDefault(protocol =>
                   string.Equals(protocol.Name, protocolName, StringComparison.OrdinalIgnoreCase))
               ?? _defaultProtocol;
    }

    private static List<AgentMessage> ApplyContextSize(
        IAgentProtocol protocol,
        List<AgentMessage> chatHistory,
        IReadOnlyList<AgentToolDefinition> toolDefinitions,
        int? contextSize)
    {
        if (contextSize is not > 0)
        {
            return chatHistory;
        }

        var promptHistory = chatHistory.ToList();
        while (promptHistory.Count > 1 &&
               EstimateTokenCount(protocol.BuildPrompt(promptHistory, toolDefinitions)) > contextSize.Value)
        {
            if (!RemoveOldestNonSystemMessage(promptHistory))
            {
                break;
            }
        }

        return promptHistory;
    }

    private static bool RemoveOldestNonSystemMessage(List<AgentMessage> messages)
    {
        var removableIndex = messages.FindIndex(message => message.Role != AgentRole.System);
        if (removableIndex < 0 || removableIndex == messages.Count - 1)
        {
            return false;
        }

        messages.RemoveAt(removableIndex);

        var firstNonSystemIndex = messages.FindIndex(message => message.Role != AgentRole.System);
        while (firstNonSystemIndex >= 0 &&
               firstNonSystemIndex < messages.Count - 1 &&
               messages[firstNonSystemIndex].Role is AgentRole.Assistant or AgentRole.Tool)
        {
            messages.RemoveAt(firstNonSystemIndex);
            firstNonSystemIndex = messages.FindIndex(message => message.Role != AgentRole.System);
        }

        return true;
    }

    private static int EstimateTokenCount(string text)
    {
        return (int)Math.Ceiling(text.Length / 4.0);
    }

    private static string CleanAssistantText(IAgentProtocol protocol, string response)
    {
        return protocol.CleanResponse(response);
    }
}
