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
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Agents;

public class JarvisAgent
{
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolRouter _toolRouter;
    private readonly IAgentProtocol _protocol;
    private readonly ILogger<JarvisAgent> _logger;

    public JarvisAgent(
        ToolRegistry toolRegistry,
        ToolRouter toolRouter,
        IAgentProtocol protocol,
        ILogger<JarvisAgent> logger)
    {
        _toolRegistry = toolRegistry;
        _toolRouter = toolRouter;
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

            var prompt = _protocol.BuildPrompt(chatHistory, toolDefinitions);
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

    private string CleanAssistantText(string response)
    {
        return _protocol is QwenProtocol qwenProtocol
            ? qwenProtocol.StripThinking(response)
            : response.Trim();
    }
}
