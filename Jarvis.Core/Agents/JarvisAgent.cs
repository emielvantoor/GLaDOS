using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Interfaces;
using Jarvis.Core.Models;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Agents;

public class JarvisAgent
{
    private readonly Dictionary<string, IJarvisTool> _tools;
    private readonly ILogger<JarvisAgent> _logger;

    public JarvisAgent(IEnumerable<IJarvisTool> tools, ILogger<JarvisAgent> logger)
    {
        _tools = tools.ToDictionary(t => t.Name);
        _logger = logger;
    }

    public async IAsyncEnumerable<string> RunAsync(
        LanguageModel model,
        List<AgentMessage> chatHistory,
        ChatOptions options,
        List<AgentToolDefinition>? externalTools = null, 
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting JarvisAgent.RunAsync");

        var toolDefinitions = _tools.Values
            .Select(t => new AgentToolDefinition(t.Name, t.Description, t.Parameters))
            .ToList();

        if (externalTools != null)
        {
            toolDefinitions.AddRange(externalTools);
        }

        int maxIterations = 5;
        int currentIteration = 0;
        bool keepRunning = true;

        while (keepRunning && currentIteration < maxIterations)
        {
            currentIteration++;

            var responseStream =
                model.GenerateChatResponseAsync(chatHistory, options, toolDefinitions, cancellationToken);

            bool isToolCallDetected = false;
            string? activeToolName = null;
            string? activeToolArgs = null;

            await foreach (var chunk in responseStream)
            {
                if (cancellationToken.IsCancellationRequested) break;

                if (chunk.IsToolCall)
                {
                    isToolCallDetected = true;
                    activeToolName = chunk.ToolName;
                    activeToolArgs = chunk.ToolArgs;
                    continue;
                }

                if (!isToolCallDetected && !string.IsNullOrEmpty(chunk.Text))
                {
                    yield return chunk.Text;
                }
            }

            // Als er een tool call is gedetecteerd:
            if (isToolCallDetected && !string.IsNullOrEmpty(activeToolName))
            {
                // CHECK: Is dit een INTERNE tool van Jarvis?
                if (_tools.TryGetValue(activeToolName, out var tool))
                {
                    // === NIEUW: Informeer de gebruiker via de stream ===
                    yield return $"\n[Systeem: Executing internal tool '{activeToolName}' with arguments: {activeToolArgs}...]\n";

                    chatHistory.Add(new AgentMessage(AgentRole.Assistant, "", activeToolName, activeToolArgs));
                    var jsonArgs = JsonNode.Parse(activeToolArgs ?? "{}")!.AsObject();
                    string toolOutput = await tool.ExecuteAsync(jsonArgs);
                    chatHistory.Add(new AgentMessage(AgentRole.Tool, toolOutput));

                    _logger.LogInformation("Tool {ToolName} executed successfully", activeToolName);
                    continue; // Blijf intern loopen
                }
                else
                {
                    // EXTENSIE: Dit is een EXTERNE tool van Rider!
                    // === NIEUW: Informeer de gebruiker via de stream ===
                    yield return $"\n[Systeem: Delegating external tool '{activeToolName}' to client...]\n";

                    string toolCallPayload = $"__TOOL_CALL__:{activeToolName}|{activeToolArgs}";
                    yield return toolCallPayload;

                    _logger.LogInformation("External tool call detected: {ToolName}", activeToolName);
                    break; // Stop de loop, geef de controle terug aan Rider via de controller!
                }
            }

            keepRunning = false;
        }

        _logger.LogInformation("Finished JarvisAgent.RunAsync");
    }
}