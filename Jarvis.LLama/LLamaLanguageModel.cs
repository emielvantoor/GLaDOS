using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
        if (_initialized) return Task.CompletedTask;

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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string formattedPrompt = FormatHistoryToChatML(history, tools);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = chatOptions.MaxTokenLength ?? ModelMetaData.MaxOutputTokens,
            SamplingPipeline = new DefaultSamplingPipeline()
            {
                Temperature = chatOptions.Temperature ?? 0.5f,
                Seed = (uint)Random.Shared.Next(1, 100000),
                Grammar = _grammer
            },
            AntiPrompts = ["<|im_end|>"],
        };

        var executor = new StatelessExecutor(_weights!, _params);
        var fullResponseBuilder = new StringBuilder();

        // 1. Verzamel de volledige output van de LLM als één string
        await foreach (var token in executor.InferAsync(formattedPrompt, inferenceParams, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            fullResponseBuilder.Append(token);
        }

        executor.Context.Dispose();

        string fullText = fullResponseBuilder.ToString().Trim();

        // 2. Verwijder de <think>...</think> blokken volledig met Regex
        fullText = Regex.Replace(fullText, @"<think>[\s\S]*?</think>", "").Trim();

        // 3. Controleer of de output een Tool Call bevat
        if (DetectToolCall(fullText, out string rawToolContent))
        {
            string normalized = NormalizeToolCall(rawToolContent);

            if (TryParseToolCall(normalized, out var name, out var args))
            {
                yield return new ChatResponseChunk(
                    Text: normalized,
                    IsToolCall: true,
                    ToolName: name,
                    ToolArgs: args
                );
                yield break;
            }
        }

        // 4. Geen tool call? Dan is het reguliere tekst
        yield return new ChatResponseChunk(
            Text: fullText,
            IsToolCall: false
        );
    }

    // =========================================================
    // TOOL DETECTIE & PARSING (EENVOUDIGE REGEX / STRINGS)
    // =========================================================

    private bool DetectToolCall(string text, out string toolContent)
    {
        toolContent = "";

        // Check <tool_call>...</tool_call>
        var xmlMatch = Regex.Match(text, @"<tool_call>([\s\S]*?)</tool_call>");
        if (xmlMatch.Success)
        {
            toolContent = xmlMatch.Groups[1].Value.Trim();
            return true;
        }

        // Check [tool_call:...]
        var bracketMatch = Regex.Match(text, @"\[tool_call:([\s\S]*?)\]");
        if (bracketMatch.Success)
        {
            toolContent = bracketMatch.Groups[1].Value.Trim();
            return true;
        }

        // Check of de gehele tekst platte JSON is die een tool call representeert
        if (text.StartsWith("{") && text.EndsWith("}") && text.Contains("\"name\""))
        {
            toolContent = text;
            return true;
        }

        return false;
    }

    private string NormalizeToolCall(string raw)
    {
        return raw
            .Replace("```json", "")
            .Replace("```", "")
            .Trim();
    }

    private bool TryParseToolCall(string jsonString, out string name, out string args)
    {
        name = "";
        args = "";

        try
        {
            var node = JsonNode.Parse(jsonString);
            if (node == null) return false;

            name = node["name"]?.ToString()
                ?? node["function"]?["name"]?.ToString()
                ?? "";

            var argsNode = node["arguments"];
            args = argsNode is JsonObject
                ? argsNode.ToJsonString()
                : argsNode?.ToString() ?? "{}";

            return !string.IsNullOrEmpty(name);
        }
        catch
        {
            return false;
        }
    }

    // =========================================================
    // CHATML FORMATTER
    // =========================================================

    private string FormatHistoryToChatML(List<AgentMessage> history, List<AgentToolDefinition> tools)
    {
        var sb = new StringBuilder();

        if (history.All(m => m.Role != AgentRole.System))
        {
            sb.Append("<|im_start|>system\n");
            sb.Append("You are Jarvis, an autonomous AI assistant.\n");

            if (tools?.Any() == true)
            {
                sb.Append("TOOLS:\n");
                sb.Append(JsonSerializer.Serialize(tools.Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.Parameters
                })));

                sb.Append("\nReturn ONLY valid tool calls when needed.\n");
                sb.Append("Tool calls may appear after reasoning blocks (<think>...</think>).");
            }

            sb.Append("<|im_end|>\n");
        }

        foreach (var message in history)
        {
            switch (message.Role)
            {
                case AgentRole.System:
                    sb.Append($"<|im_start|>system\n{message.Content}<|im_end|>\n");
                    break;

                case AgentRole.User:
                    sb.Append($"<|im_start|>user\n{message.Content}<|im_end|>\n");
                    break;

                case AgentRole.Assistant:
                    if (!string.IsNullOrEmpty(message.ToolCallName))
                    {
                        sb.Append($"<|im_start|>assistant\n<tool_call>{{\"name\":\"{message.ToolCallName}\",\"arguments\":{message.ToolCallArgs}}}</tool_call><|im_end|>\n");
                    }
                    else
                    {
                        sb.Append($"<|im_start|>assistant\n{message.Content}<|im_end|>\n");
                    }
                    break;

                case AgentRole.Tool:
                    sb.Append($"<|im_start|>user\n<tool_response>\n{message.Content}\n</tool_response><|im_end|>\n");
                    break;
            }
        }

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