using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core.Interfaces;
using Jarvis.Core.Models;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Jarvis.LLama;

public class LLamaLanguageModel : LanguageModel, IDisposable
{
    private Grammar? _grammer;
    private readonly ModelParams _params;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private bool _isDisposed;
    private bool _initialized;

    public override LanguageModelMetaData ModelMetaData { get; }

    public LLamaLanguageModel(LanguageModelMetaData metaData, ModelParams @params)
    {
        _params = @params;
        ModelMetaData = metaData ?? throw new ArgumentNullException(nameof(metaData));
    }

    protected override Task OnInitializeAsync()
    {
        if (_initialized)
        {
            return Task.CompletedTask;
        }

        // De hardware configurator heeft _params al optimaal gevuld (ContextSize, BatchSize, Q8_0 cache, GPU)
        _weights = LLamaWeights.LoadFromFile(_params);
        _context = _weights.CreateContext(_params);

        var grammerFile = _params.ModelPath.Replace(".gguf", ".gbnf");
        if (File.Exists(grammerFile))
        {
            _grammer = new Grammar(File.ReadAllText(grammerFile), "root");
        }
        
        _initialized = true;
        
        return Task.CompletedTask;
    }

    protected override async IAsyncEnumerable<ChatResponseChunk> OnGenerateChatResponseAsync(
        List<AgentMessage> history,
        ChatOptions chatOptions,
        List<AgentToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 1. Formatteer de geschiedenis naar de vlekkeloze Engelse ChatML structuur voor Qwen
        string formattedPrompt = FormatHistoryToChatML(history, tools);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = ModelMetaData.MaxOutputTokens, // Ruimer voor complexere code-antwoorden
            SamplingPipeline = new DefaultSamplingPipeline()
            {
                Temperature = chatOptions.Temperature ?? 0.5f, // Iets ademruimte om makkelijker op te starten na een tool response,
                Seed = (uint)Random.Shared.Next(1, 100000), // Dynamisch om executor/cache-loops te voorkomen
                Grammar = _grammer
            },
            AntiPrompts = ["<|im_end|>", "</tool_call>"], // Alleen stoppen als de beurt ÉCHT voorbij is
        };

        var textBuffer = new StringBuilder();
        var toolBuffer = new StringBuilder();
        bool isToolCallActive = false;
        int bracketCount = 0;
        int totalTokensProcessed = 0;

        var executor = new StatelessExecutor(_weights!, _params);
        
        await foreach (var token in executor.InferAsync(formattedPrompt, inferenceParams, cancellationToken))
        {
            totalTokensProcessed++;

            if (!isToolCallActive)
            {
                textBuffer.Append(token);
                string currentText = textBuffer.ToString();

// Maak currentText schoon van witruimte om de ECHTE start te controleren
                string trimmedText = currentText.TrimStart();

// Een directe JSON-start is ALLEEN valide als de opgebouwde tekst 
// direct begint met '{' en GEEN markdown codeblock introduceert
                bool isDirectJsonStart = trimmedText.StartsWith("{") && !trimmedText.Contains("`");

                if (currentText.Contains("<tool_call>") || isDirectJsonStart)
                {
                    isToolCallActive = true;
                    if (token.Contains("{"))
                    {
                        bracketCount += token.Count(c => c == '{');
                    }

                    toolBuffer.Append(token);
                    continue;
                }

                // Gewone tekst (inclusief C# doc xml-tags en willekeurige accolades halverwege) streamt direct live
                yield return new ChatResponseChunk(Text: token, IsToolCall: false);
            }
            else
            {
                // We zitten in een actieve tool-call. Buffer alles onzichtbaar voor de gebruiker
                toolBuffer.Append(token);

                bracketCount += token.Count(c => c == '{');
                bracketCount -= token.Count(c => c == '}');

                bool jsonIsComplete = bracketCount == 0 && toolBuffer.ToString().Contains("{");

                // Als we met <tool_call> zijn begonnen wachten we op de sluit-tag. 
                // Anders stoppen we zodra de accolades in balans zijn.
                bool shouldCloseTool = toolBuffer.ToString().Contains("<tool_call>")
                    ? token.Contains("</tool_call>")
                    : jsonIsComplete;

                if (shouldCloseTool)
                {
                    isToolCallActive = false;
                    string rawOutput = toolBuffer.ToString();
                    toolBuffer.Clear();

                    // Maak de string grondig schoon van alle mogelijke Qwen-variaties
                    string cleanJson = rawOutput
                        .Replace("<tool_call>", "")
                        .Replace("</tool_call>", "")
                        .Replace("```json", "")
                        .Replace("```", "")
                        .Trim();

                    if (TryParseToolCall(cleanJson, out string toolName, out string toolArgs))
                    {
                        yield return new ChatResponseChunk(Text: cleanJson, IsToolCall: true, ToolName: toolName,
                            ToolArgs: toolArgs);
                    }
                }
            }
        }
        
        executor.Context.Dispose();
    }

    private bool TryParseToolCall(string jsonString, out string name, out string args)
    {
        name = "";
        args = "";
        try
        {
            var node = JsonNode.Parse(jsonString);
            if (node != null)
            {
                name = node["name"]?.ToString() ?? node["function"]?["name"]?.ToString() ?? "";

                var argsNode = node["arguments"];
                args = argsNode is JsonObject ? argsNode.ToJsonString() : argsNode?.ToString() ?? "{}";

                return !string.IsNullOrEmpty(name);
            }
        }
        catch
        {
            // Foutieve JSON gegenereerd door het model, faal geruisloos
        }

        return false;
    }

    private string FormatHistoryToChatML(List<AgentMessage> history, List<AgentToolDefinition> tools)
    {
        var sb = new StringBuilder();

        // 1. Systeemprompt met Engelse instructies en OpenAI-compatibele tool-definities
        if (history.All(m => m.Role != AgentRole.System))
        {
            sb.Append("<|im_start|>system\n");
            sb.Append("You are Jarvis, an autonomous AI assistant operating on local hardware. ");
            sb.Append("Fulfill the user's requests as accurately as possible. ");

            if (tools != null && tools.Any())
            {
                sb.Append("### TOOL USAGE RULES:\n");
                sb.Append(
                    "- If the user asks a general question, greets you (e.g., 'hello', 'hi'), or asks for something that does NOT require a tool, you MUST respond with normal conversational text. Do NOT invoke any tools.\n");
                sb.Append(
                    "- Only invoke a tool if the user's request directly requires it (for example, asking for the current time or date).\n");
                sb.Append(
                    "- To invoke a tool, you must respond EXCLUSIVELY with a JSON object containing 'name' and 'arguments'. Do not write conversational text when invoking tools.\n\n");

                sb.Append("Available tools:\n");

                var toolSchema = tools.Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.Parameters
                });

                sb.Append(JsonSerializer.Serialize(toolSchema) + "\n");
                sb.Append(
                    "When you want to use a tool, you must invoke it using the official tool_calls API structure. Never output the JSON tool call as markdown code blocks in the chat response.");
                sb.Append(
                    "When you want to execute a tool, you must use the <tool_call> tags. Do never describe the tool in chat why you would choice this tool.");
            }

            sb.Append("<|im_end|>\n");
        }

        // 2. Loop door de complete chatgeschiedenis heen
        foreach (var message in history)
        {
            switch (message.Role)
            {
                case AgentRole.System:
                    sb.Append($"<|im_start|>system\n{message.Content}");

                    if (tools?.Count > 0 && !message.Content.Contains("\"tools\""))
                    {
                        sb.Append("\n\nYou have access to the following functions/tools:\n");
                        var toolSchema = tools.Select(t => new
                        {
                            name = t.Name,
                            description = t.Description,
                            parameters = t.Parameters
                        });
                        sb.Append(JsonSerializer.Serialize(toolSchema));
                    }

                    sb.Append("<|im_end|>\n");
                    break;

                case AgentRole.User:
                    sb.Append($"<|im_start|>user\n{message.Content}<|im_end|>\n");
                    break;

                case AgentRole.Assistant:
                    // Als dit een tool-call was, herstellen we de JSON exact in de geschiedenis
                    if (!string.IsNullOrEmpty(message.ToolCallName))
                    {
                        sb.Append(
                            $"<|im_start|>assistant\n<tool_call>{{\"name\":\"{message.ToolCallName}\",\"arguments\":{message.ToolCallArgs}}}</tool_call><|im_end|>\n");
                    }
                    else
                    {
                        sb.Append($"<|im_start|>assistant\n{message.Content}<|im_end|>\n");
                    }

                    break;

                case AgentRole.Tool:
                    // De officiële ChatML manier voor Qwen om een tool resultaat te verwerken
                    sb.Append(
                        $"<|im_start|>user\n<tool_response>\n{message.Content}\n</tool_response><|im_end|>\n");
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        // 3. Open de assistent tag met een newline zodat de GPU direct weet waar hij moet beginnen te typen
        sb.Append("<|im_start|>assistant\n");

        return sb.ToString();
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _context?.Dispose();
        _weights?.Dispose();
        _isDisposed = true;

        GC.SuppressFinalize(this);
    }
}