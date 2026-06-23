using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Core.Interfaces;
using Jarvis.Core.Models;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Agents;

public class JarvisAgent
{
    private readonly Dictionary<string, IJarvisTool> _tools;

    // Alleen de tools worden via DI geïnjecteerd
    public JarvisAgent(IEnumerable<IJarvisTool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name);
    }

  public async IAsyncEnumerable<string> RunAsync(
    LanguageModel model,
    List<AgentMessage> chatHistory,
    List<AgentToolDefinition>? externalTools = null, // NIEUW: Accepteer Rider's dynamische tools
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    // 1. Combineer je eigen geïnjecteerde tools met de dynamische tools van Rider
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

        var responseStream = model.GenerateChatResponseAsync(chatHistory, toolDefinitions, cancellationToken);

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
                chatHistory.Add(new AgentMessage(AgentRole.Assistant, "", activeToolName, activeToolArgs));
                var jsonArgs = JsonNode.Parse(activeToolArgs ?? "{}")!.AsObject();
                string toolOutput = await tool.ExecuteAsync(jsonArgs);
                chatHistory.Add(new AgentMessage(AgentRole.Tool, toolOutput));
                
                continue; // Blijf intern loopen
            }
            else
            {
                // EXTENSIE: Dit is een EXTERNE tool van Rider!
                // We kunnen dit niet zelf uitvoeren. We moeten de loop direct BREKEN 
                // en een speciaal signaal (bijv. een geprepareerde JSON-string) terug 'yielden' naar de Web API.
                
                string toolCallPayload = $"__TOOL_CALL__:{activeToolName}|{activeToolArgs}";
                yield return toolCallPayload;
                
                break; // Stop de loop, geef de controle terug aan Rider via de controller!
            }
        }

        keepRunning = false;
    }
}
}