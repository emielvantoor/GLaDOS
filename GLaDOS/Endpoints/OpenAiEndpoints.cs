using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GLaDOS.Extensions;
using GLaDOS.Core.Agents;
using GLaDOS.Core.Interfaces;
using GLaDOS.Core.Models;
using GLaDOS.Core.Routing;
using GLaDOS.Core.Services;
using GLaDOS.Models;
using Microsoft.AspNetCore.Mvc;

namespace GLaDOS.Endpoints;

public static class OpenAiEndpoints
{
    private static readonly SemaphoreSlim _llmLock = new(1, 1);
    private static readonly Encoding SseEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private const string PotatoProtocolName = "GLaDOS";
    private const string QwenProtocolName = "Qwen";

    public static void MapOpenAiEndpoints(this IEndpointRouteBuilder app)
    {
        var v1Group = app.MapGroup("/v1");

        v1Group.MapGet("/models/{model}", GetSpecificModel);
        v1Group.MapGet("/models", GetModels);
        v1Group.MapGet("/runtime/memory", GetRuntimeMemoryUsage);
        v1Group.MapGet("/tools", GetTools);
        v1Group.MapPost("/tools/execute", ExecuteApprovedTool);
        v1Group.MapPost("/chat/completions", HandleChatCompletions);
    }

    private static IResult GetTools([FromServices] ToolRegistry toolRegistry)
    {
        var tools = toolRegistry.GetDefinitions()
            .Select(tool => new
            {
                type = "function",
                source = "Internal",
                permitted = tool.Permitted,
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = tool.Parameters
                }
            });

        return Results.Ok(new { data = tools });
    }

    private static async Task<IResult> ExecuteApprovedTool(
        [FromServices] ToolRegistry toolRegistry,
        [FromBody] ToolExecutionRequest? request,
        HttpContext context)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = new { message = "Tool name is required.", type = "invalid_request_error" } });
        }

        if (!toolRegistry.TryGetInternalTool(request.Name, out var tool))
        {
            return Results.NotFound(new { error = new { message = $"Tool '{request.Name}' not found.", type = "not_found_error" } });
        }

        try
        {
            var output = await tool.ExecuteAsync(request.Arguments ?? new JsonObject());
            return Results.Ok(new ToolExecutionResponse(request.Name, output));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            return Results.Problem(detail: $"Failed to execute tool '{request.Name}': {ex.Message}", statusCode: 500);
        }
    }

    /// <summary>
    /// Handles OpenAI compliant chat completions supporting standard text and tool-call SSE streaming.
    /// </summary>
    private static async Task<IResult> HandleChatCompletions(
        [FromServices] IModelManager modelManager,
        [FromServices] GLaDOSAgent agent,
        [FromBody] ChatCompletionRequest? request,
        HttpContext context)
    {
        var token = context.RequestAborted;

        // 1. Basic Validation
        if (request?.Messages == null || !request.Messages.Any())
        {
            return Results.BadRequest(new { error = new { message = "Invalid request or empty messages list.", type = "invalid_request_error" } });
        }

        // 2. Concurrency Lock
        try
        {
            await _llmLock.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }

        Console.WriteLine($"Handling Chat Completions for model: {request.Model}");

        // 3. Initialize LLM Model
        LanguageModel model;
        try
        {
            model = await modelManager.GetAndInitializeModel(request.Model);
        }
        catch (Exception ex)
        {
            _llmLock.Release();
            return Results.Problem(detail: $"Failed to load model: {ex.Message}", statusCode: 500);
        }

        // 4. Transform to internal domain architecture
        var domainMessages = request.Messages.Select(message => message.ToDomainModel()).ToList();
        var domainTools = request.Tools?.Select(tool => tool.ToDomainModel()).ToList() ?? [];
        var protocolName = SelectProtocolName(request, domainTools);

        var agentResultStream = agent.RunAsync(model, domainMessages, new ChatOptions
        {
            Temperature = request.Temperature,
            ContextSize = request.ContextSize,
            MaxTokenLength = request.MaxCompletionTokens ?? request.MaxTokenLength
        }, domainTools, protocolName, token);

        var serializerOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Metadata setup for OpenAI specifications
        string chunkId = $"chatcmpl-{Guid.NewGuid()}";
        long createdTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string modelName = request.Model ?? "local-model";

        if (!request.Stream)
        {
            return await CreateBufferedResponse(
                agentResultStream,
                chunkId,
                createdTimestamp,
                modelName,
                token);
        }

        // 5. Response SSE Stream
        return Results.Stream(async stream =>
        {
            try
            {
                await using var writer = new StreamWriter(stream, SseEncoding, leaveOpen: true) { NewLine = "\n" };
                bool toolCallTriggered = false;

                await WriteSseAsync(
                    writer,
                    CreateChunk(chunkId, createdTimestamp, modelName, new ChatDelta { Role = "assistant" }),
                    serializerOptions,
                    token);

                await foreach (var text in agentResultStream.WithCancellation(token))
                {
                    if (token.IsCancellationRequested) break;

                    if (IsInternalStatusMessage(text))
                    {
                        continue;
                    }
                
                    // Intercept and parse internal legacy __TOOL_CALL__ syntax
                    if (TryParseToolCall(text, out var toolCall))
                    {
                        toolCallTriggered = true;
                        await WriteSseAsync(
                            writer,
                            CreateChunk(
                                chunkId,
                                createdTimestamp,
                                modelName,
                                new ChatDelta { ToolCalls = [CreateStreamingToolCall(toolCall, 0)] }),
                            serializerOptions,
                            token);
                        await WriteSseAsync(
                            writer,
                            CreateChunk(
                                chunkId,
                                createdTimestamp,
                                modelName,
                                new ChatDelta(),
                                "tool_calls"),
                            serializerOptions,
                            token);
                        break; 
                    }

                    // Handle Standard Text Token Stream
                    await WriteSseAsync(
                        writer,
                        CreateChunk(chunkId, createdTimestamp, modelName, new ChatDelta { Content = text }),
                        serializerOptions,
                        token);
                }

                // Finalize text response chunk if a tool call was not triggered
                if (!toolCallTriggered && !token.IsCancellationRequested)
                {
                    await WriteSseAsync(
                        writer,
                        CreateChunk(chunkId, createdTimestamp, modelName, new ChatDelta(), "stop"),
                        serializerOptions,
                        token);
                }

                // Standard end of SSE protocol payload
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync(token);
            }
            finally
            {
                Console.WriteLine("Complete response generated via stream.");
                _llmLock.Release();
            }
        }, "text/event-stream");
    }

    private static async Task<IResult> CreateBufferedResponse(
        IAsyncEnumerable<string> agentResultStream,
        string responseId,
        long createdTimestamp,
        string modelName,
        CancellationToken token)
    {
        var responseBuffer = new StringBuilder();

        try
        {
            await foreach (var text in agentResultStream.WithCancellation(token))
            {
                if (IsInternalStatusMessage(text))
                {
                    continue;
                }

                if (TryParseToolCall(text, out var toolCall))
                {
                    return Results.Ok(new ChatCompletionResponse
                    {
                        Id = responseId,
                        Created = createdTimestamp,
                        Model = modelName,
                        Choices =
                        [
                            new ChatChoice
                            {
                                Message = new ChatMessage
                                {
                                    Role = "assistant",
                                    ToolCalls = [toolCall]
                                },
                                FinishReason = "tool_calls"
                            }
                        ]
                    });
                }

                responseBuffer.Append(text);
            }

            return Results.Ok(new ChatCompletionResponse
            {
                Id = responseId,
                Created = createdTimestamp,
                Model = modelName,
                Choices =
                [
                    new ChatChoice
                    {
                        Message = new ChatMessage
                        {
                            Role = "assistant",
                            Content = responseBuffer.ToString()
                        },
                        FinishReason = "stop"
                    }
                ]
            });
        }
        finally
        {
            Console.WriteLine("Complete response generated.");
            _llmLock.Release();
        }
    }

    private static ChatCompletionChunk CreateChunk(
        string id,
        long created,
        string model,
        ChatDelta delta,
        string? finishReason = null)
    {
        return new ChatCompletionChunk
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new ChatChunkChoice
                {
                    Delta = delta,
                    FinishReason = finishReason
                }
            ]
        };
    }

    private static async Task WriteSseAsync(
        StreamWriter writer,
        ChatCompletionChunk chunk,
        JsonSerializerOptions serializerOptions,
        CancellationToken token)
    {
        var json = JsonSerializer.Serialize(chunk, serializerOptions);
        await writer.WriteAsync($"data: {json}\n\n");
        await writer.FlushAsync(token);
    }

    private static bool TryParseToolCall(string? text, out ChatCompletionToolCall toolCall)
    {
        toolCall = new ChatCompletionToolCall();

        if (string.IsNullOrEmpty(text) || !text.StartsWith("__TOOL_CALL__:", StringComparison.Ordinal))
        {
            return false;
        }

        var payload = text["__TOOL_CALL__:".Length..];
        var separatorIndex = payload.IndexOf('|');
        var name = separatorIndex >= 0 ? payload[..separatorIndex] : payload;
        var arguments = separatorIndex >= 0 ? payload[(separatorIndex + 1)..] : "{}";

        if (separatorIndex < 0 && payload.TrimStart().StartsWith('{'))
        {
            try
            {
                var node = JsonNode.Parse(payload);
                var parsedName = node?["name"]?.ToString()
                    ?? node?["function"]?["name"]?.ToString();

                if (!string.IsNullOrWhiteSpace(parsedName))
                {
                    name = parsedName;
                    arguments = (node?["arguments"] ?? node?["function"]?["arguments"])?.ToJsonString() ?? "{}";
                }
            }
            catch (JsonException)
            {
                // Fall back to the legacy payload parsing below.
            }
        }

        toolCall = new ChatCompletionToolCall
        {
            Id = $"call_{Guid.NewGuid():n}",
            Type = "function",
            Function = new ChatCompletionToolCallFunction
            {
                Name = name,
                Arguments = arguments
            }
        };

        return true;
    }

    private static ChatCompletionToolCall CreateStreamingToolCall(ChatCompletionToolCall toolCall, int index)
    {
        return new ChatCompletionToolCall
        {
            Index = index,
            Id = toolCall.Id,
            Type = toolCall.Type,
            Function = toolCall.Function
        };
    }

    private static bool IsInternalStatusMessage(string? text)
    {
        return !string.IsNullOrWhiteSpace(text)
               && text.TrimStart().StartsWith("[Systeem:", StringComparison.Ordinal);
    }

    private static string? SelectProtocolName(
        ChatCompletionRequest request,
        IReadOnlyList<AgentToolDefinition> domainTools)
    {
        if (LooksLikeQwenAgentRequest(request))
        {
            return QwenProtocolName;
        }

        return LooksLikePotatoRequest(request, domainTools)
            ? PotatoProtocolName
            : null;
    }

    private static bool LooksLikeQwenAgentRequest(ChatCompletionRequest request)
    {
        return ContainsQwenAgentIdentifier(request.Model) ||
               request.Messages.Any(message =>
                   string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase) &&
                   ContainsQwenAgentIdentifier(message.Content));
    }

    private static bool ContainsQwenAgentIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("You are Qwen Code", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePotatoRequest(
        ChatCompletionRequest request,
        IReadOnlyList<AgentToolDefinition> domainTools)
    {
        if (request.Messages.Any(message =>
                string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase) &&
                message.Content?.Contains("You are PotatOS", StringComparison.Ordinal) == true))
        {
            return true;
        }

        return domainTools.Any(tool =>
            string.Equals(tool.Name, "ApplySearchReplaceAsync", StringComparison.Ordinal) ||
            string.Equals(tool.Name, "CreateFileAsync", StringComparison.Ordinal) ||
            string.Equals(tool.Name, "ApplyDiffPatchAsync", StringComparison.Ordinal));
    }

    private sealed record ToolExecutionRequest(string Name, JsonObject? Arguments);

    private sealed record ToolExecutionResponse(string Name, string Output);

    private static IResult GetModels([FromServices] IModelManager modelManager)
    {
        var modelsList = new ModelListResponse();
        var models = modelManager.GetAvailableModels();
        modelsList.Data.AddRange(models.Select(m => m.ToDto()));

        return Results.Ok(modelsList);
    }

    private static IResult GetSpecificModel([FromRoute] string model, [FromServices] IModelManager modelManager)
    {
        var models = modelManager.GetAvailableModels();
        var dtoModel = models.FirstOrDefault(m => m.Id == model)?.ToDto();
        
        if (dtoModel == null)
        {
            return Results.NotFound(new { error = new { message = $"Model '{model}' not found.", type = "invalid_request_error" } });
        }
        
        return Results.Ok(dtoModel);
    }

    private static IResult GetRuntimeMemoryUsage()
    {
        var process = Process.GetCurrentProcess();
        var response = new RuntimeMemoryUsageResponse
        {
            ProcessRamMb = BytesToMegabytes(process.WorkingSet64),
            ManagedHeapMb = BytesToMegabytes(GC.GetTotalMemory(forceFullCollection: false))
        };

        if (TryGetSystemMemory(out var systemUsedMb, out var systemTotalMb))
        {
            response.SystemRamUsedMb = systemUsedMb;
            response.SystemRamTotalMb = systemTotalMb;
        }

        if (TryGetNvidiaMemory(out var nvidiaUsedMb, out var nvidiaTotalMb, out var nvidiaName))
        {
            response.GpuVramUsedMb = nvidiaUsedMb;
            response.GpuVramTotalMb = nvidiaTotalMb;
            response.GpuName = nvidiaName;
            response.GpuSource = "nvidia-smi";
        }
        else if (TryGetRocmMemory(out var rocmUsedMb, out var rocmTotalMb, out var rocmName))
        {
            response.GpuVramUsedMb = rocmUsedMb;
            response.GpuVramTotalMb = rocmTotalMb;
            response.GpuName = rocmName;
            response.GpuSource = "rocm-smi";
        }

        return Results.Ok(response);
    }

    private static bool TryGetSystemMemory(out double usedMb, out double totalMb)
    {
        usedMb = 0;
        totalMb = 0;

        if (!File.Exists("/proc/meminfo"))
        {
            return false;
        }

        var values = File.ReadLines("/proc/meminfo")
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => parts[0],
                parts => ParseFirstNumber(parts[1]),
                StringComparer.OrdinalIgnoreCase);

        if (!values.TryGetValue("MemTotal", out var totalKb))
        {
            return false;
        }

        values.TryGetValue("MemAvailable", out var availableKb);
        totalMb = totalKb / 1024;
        usedMb = (totalKb - availableKb) / 1024;
        return true;
    }

    private static bool TryGetNvidiaMemory(out double usedMb, out double totalMb, out string? gpuName)
    {
        usedMb = 0;
        totalMb = 0;
        gpuName = null;

        var output = RunCommand(
            "nvidia-smi",
            "--query-gpu=name,memory.used,memory.total --format=csv,noheader,nounits");

        var firstLine = output?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return false;
        }

        var parts = firstLine.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3 ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out usedMb) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out totalMb))
        {
            return false;
        }

        gpuName = parts[0];
        return true;
    }

    private static bool TryGetRocmMemory(out double usedMb, out double totalMb, out string? gpuName)
    {
        usedMb = 0;
        totalMb = 0;
        gpuName = null;

        var totalOutput = RunCommand("rocm-smi", "--showmeminfo vram --csv");
        if (!TryParseRocmVramInfo(totalOutput, out var device, out usedMb, out totalMb))
        {
            return false;
        }

        var nameOutput = RunCommand("rocm-smi", "--showproductname --csv");
        gpuName = ParseRocmGpuName(nameOutput, device);

        return true;
    }

    private static bool TryParseRocmVramInfo(
        string? output,
        out string? device,
        out double usedMb,
        out double totalMb)
    {
        device = null;
        usedMb = 0;
        totalMb = 0;

        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var bestTotalBytes = 0d;
        var bestUsedBytes = 0d;
        string? bestDevice = null;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("card", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var totalBytes) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var usedBytes))
            {
                continue;
            }

            if (totalBytes <= bestTotalBytes)
            {
                continue;
            }

            bestDevice = parts[0];
            bestTotalBytes = totalBytes;
            bestUsedBytes = usedBytes;
        }

        if (bestTotalBytes <= 0)
        {
            return false;
        }

        device = bestDevice;
        usedMb = BytesToMegabytes((long)bestUsedBytes);
        totalMb = BytesToMegabytes((long)bestTotalBytes);
        return true;
    }

    private static string? ParseRocmGpuName(string? output, string? device)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(device) ||
                !line.StartsWith(device, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            var series = parts[1];
            var model = parts[2];

            if (!string.IsNullOrWhiteSpace(series) && !series.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            {
                return series;
            }

            if (!string.IsNullOrWhiteSpace(model) && !model.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }
        }

        return null;
    }

    private static string? RunCommand(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process == null || !process.WaitForExit(1500) || process.ExitCode != 0)
            {
                return null;
            }

            return process.StandardOutput.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static double ParseFirstNumber(string text)
    {
        var number = new string(text
            .SkipWhile(c => !char.IsDigit(c) && c != '.')
            .TakeWhile(c => char.IsDigit(c) || c == '.')
            .ToArray());

        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static double BytesToMegabytes(long bytes)
    {
        return bytes / 1024d / 1024d;
    }
}
