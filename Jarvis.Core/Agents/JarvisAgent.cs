using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Interfaces;
using Jarvis.Core.Models;
using Jarvis.Core.Protocols;
using Jarvis.Core.Routing;
using Jarvis.Core.ToolAdapters;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Agents;

public class JarvisAgent
{
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolRouter _toolRouter;
    private readonly ToolCallAdapterPipeline _toolCallAdapterPipeline;
    private readonly IAgentProtocol _protocol;
    private readonly ILogger<JarvisAgent> _logger;

    public JarvisAgent(
        ToolRegistry toolRegistry,
        ToolRouter toolRouter,
        ToolCallAdapterPipeline toolCallAdapterPipeline,
        IAgentProtocol protocol,
        ILogger<JarvisAgent> logger)
    {
        _toolRegistry = toolRegistry;
        _toolRouter = toolRouter;
        _toolCallAdapterPipeline = toolCallAdapterPipeline;
        _protocol = protocol;
        _logger = logger;
    }

    public async IAsyncEnumerable<string> RunAsync(
        LanguageModel model,
        List<AgentMessage> chatHistory,
        ChatOptions options,
        List<AgentToolDefinition>? externalTools = null, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting JarvisAgent.RunAsync with {Protocol} protocol", _protocol.Name);

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

            var promptHistory = ApplyContextSize(chatHistory, toolDefinitions, options.ContextSize);
            var prompt = _protocol.BuildPrompt(promptHistory, toolDefinitions);
            var response = await model.GenerateResponseAsync(prompt, options, cancellationToken);
            var toolCall = _protocol.ParseResponse(response).FirstOrDefault();

            if (toolCall == null)
            {
                var text = CleanAssistantText(response);
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

        _logger.LogInformation("Finished JarvisAgent.RunAsync");
    }

    private List<AgentMessage> ApplyContextSize(
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
               EstimateTokenCount(_protocol.BuildPrompt(promptHistory, toolDefinitions)) > contextSize.Value)
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

    private string CleanAssistantText(string response)
    {
        return _protocol is QwenProtocol qwenProtocol
            ? qwenProtocol.StripThinking(response)
            : response.Trim();
    }
}
