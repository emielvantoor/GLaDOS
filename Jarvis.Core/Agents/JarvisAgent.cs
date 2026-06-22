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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int maxIterations = 5;
        int currentIteration = 0;
        bool keepRunning = true;

        var toolDefinitions = _tools.Values.Select(t => new AgentToolDefinition(t.Name, t.Description, t.Parameters))
            .ToList();

        while (keepRunning && currentIteration < maxIterations)
        {
            currentIteration++;

            var responseStream = model.GenerateChatResponseAsync(chatHistory, toolDefinitions, cancellationToken);

            // CRUCIAAL: Reset deze vlaggen bij ELKE nieuwe denk-ronde van de loop!
            bool isToolCallDetected = false;
            string? activeToolName = null;
            string? activeToolArgs = null;

            await foreach (var chunk in responseStream)
            {
                if (chunk.IsToolCall)
                {
                    isToolCallDetected = true;
                    activeToolName = chunk.ToolName;
                    activeToolArgs = chunk.ToolArgs;
                    continue; // Spring naar volgende chunk, stream niks naar de UI!
                }

                // Stream ALLEEN naar de UI als er in deze specifieke ronde GEEN tool call bezig is
                if (!isToolCallDetected && !string.IsNullOrEmpty(chunk.Text))
                {
                    yield return chunk.Text;
                }
            }

            // Als de GPU klaar is met praten/denken en een tool_call heeft klaargezet:
            if (isToolCallDetected && !string.IsNullOrEmpty(activeToolName) &&
                _tools.TryGetValue(activeToolName, out var tool))
            {
                // 1. Sla de beslissing van de assistent op
                chatHistory.Add(new AgentMessage(AgentRole.Assistant, "", activeToolName, activeToolArgs));

                // 2. Voer de tool geruisloos uit in C# (zonder te yielden naar de UI!)
                var jsonArgs = JsonNode.Parse(activeToolArgs ?? "{}")!.AsObject();
                string toolOutput = await tool.ExecuteAsync(jsonArgs);

                // 3. Voeg het resultaat toe aan de geschiedenis
                chatHistory.Add(new AgentMessage(AgentRole.Tool, toolOutput));

                // 4. Blijf in de while-loop zodat het model in de volgende ronde de tijd kan vertalen naar tekst!
                continue;
            }

            // Als het model in deze ronde gewoon tekst heeft gespugd zonder tools, zijn we helemaal klaar.
            keepRunning = false;
        }
    }
}