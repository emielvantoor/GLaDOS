using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Potato.WebUi;

namespace Potato.Acp;

/// <summary>
/// A deliberately small ACP v1 agent transport. It keeps ACP framing and state out of
/// the interactive console so an editor can run Potato without terminal prompts corrupting
/// the JSON-RPC stream.
/// </summary>
internal sealed class AcpAgentServer(
    IChatClient chatClient,
    string model,
    PotatoWebUiReporter webUiReporter,
    Uri gladosEndpoint,
    GladosChatClientFactory clientFactory,
    int contextSize)
{
    private const int ProtocolVersion = 1;
    private readonly ConcurrentDictionary<string, AcpSession> sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim outputLock = new(1, 1);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await Console.In.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            _ = DispatchAsync(line, cancellationToken);
        }
    }

    private async Task DispatchAsync(string line, CancellationToken serverCancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return; // ACP notifications are allowed to be best-effort; no id means no reply is possible.
        }

        using (document)
        {
            JsonElement request = document.RootElement;
            string? method = request.TryGetProperty("method", out JsonElement methodElement)
                ? methodElement.GetString()
                : null;
            JsonNode? id = request.TryGetProperty("id", out JsonElement idElement)
                ? JsonNode.Parse(idElement.GetRawText())
                : null;
            JsonElement parameters = request.TryGetProperty("params", out JsonElement paramsElement)
                ? paramsElement
                : default;

            if (string.IsNullOrWhiteSpace(method))
            {
                await ReplyErrorAsync(id, -32600, "Invalid JSON-RPC request.");
                return;
            }

            try
            {
                JsonNode? result = await HandleMethodAsync(method, parameters, serverCancellationToken);

                if (id is not null)
                {
                    await WriteAsync(new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id,
                        ["result"] = result
                    });
                }
            }
            catch (AcpRequestException exception)
            {
                await ReplyErrorAsync(id, exception.Code, exception.Message);
            }
            catch (OperationCanceledException)
            {
                await ReplyErrorAsync(id, -32800, "Request cancelled.");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"ACP request failed: {exception}");
                await ReplyErrorAsync(id, -32603, "Potato could not complete the request.");
            }
        }
    }

    private async Task<JsonNode?> HandleMethodAsync(string method, JsonElement parameters, CancellationToken cancellationToken) =>
        method switch
        {
            "initialize" => Initialize(parameters),
            "session/new" => await NewSessionAsync(parameters),
            "session/prompt" => await PromptAsync(parameters, cancellationToken),
            "session/cancel" => Cancel(parameters),
            "session/close" => Close(parameters),
            "session/set_config_option" => await SetConfigOptionAsync(parameters),
            _ => throw new AcpRequestException(-32601, $"Unsupported ACP method '{method}'.")
        };

    private JsonObject Initialize(JsonElement parameters)
    {
        int requestedVersion = GetInt(parameters, "protocolVersion");
        if (requestedVersion != ProtocolVersion)
        {
            throw new AcpRequestException(-32602, $"Potato supports ACP protocol version {ProtocolVersion}.");
        }

        return new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["agentInfo"] = new JsonObject { ["name"] = "Potato", ["version"] = "ACP v1", ["title"] = model },
            ["agentCapabilities"] = new JsonObject
            {
                // Potato's permissioned local tools are intentionally not advertised until
                // their ACP permission bridge is available. Prompt-only ACP remains safe.
                ["loadSession"] = false
            }
        };
    }

    private async Task<JsonObject> NewSessionAsync(JsonElement parameters)
    {
        string workingDirectory = GetRequiredString(parameters, "cwd");
        string sessionId = Guid.NewGuid().ToString("N");
        var session = new AcpSession(sessionId, Path.GetFullPath(workingDirectory), chatClient, model);
        sessions[sessionId] = session;
        webUiReporter.Record("status", "status", $"ACP session {sessionId[..8]} created for {workingDirectory}.", collapsed: true);
        await NotifyAvailableCommandsAsync(sessionId);

        return new JsonObject
        {
            ["sessionId"] = sessionId,
            ["configOptions"] = await BuildConfigOptionsAsync(session)
        };
    }

    private async Task<JsonObject> PromptAsync(JsonElement parameters, CancellationToken serverCancellationToken)
    {
        string sessionId = GetRequiredString(parameters, "sessionId");
        if (!sessions.TryGetValue(sessionId, out AcpSession? session))
        {
            throw new AcpRequestException(-32602, "Unknown ACP session.");
        }

        string prompt = GetPromptText(parameters);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new AcpRequestException(-32602, "session/prompt requires a text content block.");
        }

        if (prompt.StartsWith("/", StringComparison.Ordinal))
        {
            string commandResponse = await ExecuteCommandAsync(session, prompt, serverCancellationToken);
            await NotifyAsync(sessionId, "agent_message_chunk", commandResponse);
            webUiReporter.Record("message", "assistant", commandResponse, collapsed: false);
            return new JsonObject { ["stopReason"] = "end_turn" };
        }

        await session.Gate.WaitAsync(serverCancellationToken);
        try
        {
            using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                serverCancellationToken,
                session.StartPromptCancellation());
            webUiReporter.Record("message", "user", prompt, collapsed: false);
            await NotifyAsync(sessionId, "agent_thought_chunk", "Potato is processing the ACP prompt.");

            List<ChatMessage> history = session.History;
            history.Add(new ChatMessage(ChatRole.User, prompt));
            ChatResponse response = await session.ChatClient.GetResponseAsync(history, new ChatOptions(), linkedCancellation.Token);
            string text = string.IsNullOrWhiteSpace(response.Text) ? "No response was returned." : response.Text.Trim();
            history.Add(new ChatMessage(ChatRole.Assistant, text));
            webUiReporter.Record("message", "assistant", text, collapsed: false);
            await NotifyAsync(sessionId, "agent_message_chunk", text);

            return new JsonObject { ["stopReason"] = "end_turn" };
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private JsonObject Cancel(JsonElement parameters)
    {
        string sessionId = GetRequiredString(parameters, "sessionId");
        if (sessions.TryGetValue(sessionId, out AcpSession? session))
        {
            session.CancelPrompt();
        }

        return new JsonObject();
    }

    private JsonObject Close(JsonElement parameters)
    {
        string sessionId = GetRequiredString(parameters, "sessionId");
        if (sessions.TryRemove(sessionId, out AcpSession? session))
        {
            session.Dispose();
        }

        return new JsonObject();
    }

    private async Task<JsonObject> SetConfigOptionAsync(JsonElement parameters)
    {
        string sessionId = GetRequiredString(parameters, "sessionId");
        string configId = GetRequiredString(parameters, "configId");
        string value = GetRequiredString(parameters, "value");
        if (!sessions.TryGetValue(sessionId, out AcpSession? session))
        {
            throw new AcpRequestException(-32602, "Unknown ACP session.");
        }

        if (!configId.Equals("model", StringComparison.Ordinal))
        {
            throw new AcpRequestException(-32602, $"Unknown session configuration option '{configId}'.");
        }

        string result = await SetModelAsync(session, value);
        if (!result.StartsWith("Selected model:", StringComparison.Ordinal))
        {
            throw new AcpRequestException(-32602, result);
        }

        JsonArray configOptions = await BuildConfigOptionsAsync(session);
        await NotifyConfigOptionsAsync(sessionId, configOptions);
        return new JsonObject { ["configOptions"] = configOptions };
    }

    private async Task NotifyAvailableCommandsAsync(string sessionId)
    {
        JsonArray commands =
        [
            Command("model", "Show available models or select one.", "[model-id]"),
            Command("cd", "Change this ACP session's working directory.", "<path>"),
            Command("ask", "Ask a one-off question without changing the session history.", "<question>"),
            Command("sessions", "List active ACP sessions."),
            Command("transcript", "Show this ACP session's transcript."),
            Command("abort", "Cancel current work and clear this session's conversation history.")
        ];

        await WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "session/update",
            ["params"] = new JsonObject
            {
                ["sessionId"] = sessionId,
                ["update"] = new JsonObject
                {
                    ["sessionUpdate"] = "available_commands_update",
                    ["availableCommands"] = commands
                }
            }
        });
    }

    private async Task<JsonArray> BuildConfigOptionsAsync(AcpSession session)
    {
        List<string> models = await ModelSelector.GetAvailableModelsAsync(gladosEndpoint);
        if (!models.Contains(session.Model, StringComparer.OrdinalIgnoreCase)) models.Insert(0, session.Model);

        return
        [
            new JsonObject
            {
                ["id"] = "model",
                ["name"] = "Model",
                ["description"] = "The GLaDOS model used for this ACP session.",
                ["category"] = "model",
                ["type"] = "select",
                ["currentValue"] = session.Model,
                ["options"] = new JsonArray(models.Select(value => new JsonObject
                {
                    ["value"] = value,
                    ["name"] = value
                }).ToArray())
            }
        ];
    }

    private Task NotifyConfigOptionsAsync(string sessionId, JsonArray configOptions) =>
        WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "session/update",
            ["params"] = new JsonObject
            {
                ["sessionId"] = sessionId,
                ["update"] = new JsonObject
                {
                    ["sessionUpdate"] = "config_option_update",
                    ["configOptions"] = configOptions
                }
            }
        });

    private static JsonObject Command(string name, string description, string? hint = null) =>
        new()
        {
            ["name"] = name,
            ["description"] = description,
            ["input"] = hint is null ? null : new JsonObject { ["hint"] = hint }
        };

    private async Task<string> ExecuteCommandAsync(AcpSession session, string input, CancellationToken cancellationToken)
    {
        (string command, string arguments) = SplitCommand(input);
        switch (command)
        {
            case "/model":
                return await ExecuteModelCommandAsync(session, arguments);
            case "/cd":
                return ExecuteChangeDirectoryCommand(session, arguments);
            case "/ask":
                return await ExecuteSideQuestionCommandAsync(session, arguments, cancellationToken);
            case "/sessions":
                return DescribeSessions();
            case "/transcript":
                return DescribeTranscript(session);
            case "/abort":
                session.CancelPrompt();
                session.History.Clear();
                return "Aborted current work and cleared this ACP session's conversation history.";
            default:
                return $"Unknown command: {command}. Available commands: /model, /cd, /ask, /sessions, /transcript, /abort.";
        }
    }

    private async Task<string> ExecuteModelCommandAsync(AcpSession session, string arguments)
    {
        List<string> models = await ModelSelector.GetAvailableModelsAsync(gladosEndpoint);
        string requestedModel = arguments.Trim();
        if (requestedModel.Length == 0)
        {
            string available = models.Count == 0 ? "No models were returned by GLaDOS." : string.Join("\n", models.Select(value => $"- {value}"));
            return $"Current model: {session.Model}\n\nAvailable models:\n{available}\n\nUse /model <model-id> to switch.";
        }

        string result = await SetModelAsync(session, requestedModel, models);
        return result;
    }

    private async Task<string> SetModelAsync(AcpSession session, string requestedModel, List<string>? availableModels = null)
    {
        List<string> models = availableModels ?? await ModelSelector.GetAvailableModelsAsync(gladosEndpoint);
        string? selectedModel = models.FirstOrDefault(value => value.Equals(requestedModel, StringComparison.OrdinalIgnoreCase));
        if (selectedModel is null)
        {
            return models.Count == 0
                ? "GLaDOS returned no models, so Potato cannot validate the requested model."
                : $"Unknown model: {requestedModel}";
        }

        session.ChatClient = clientFactory.CreateOpenAiClient(gladosEndpoint, selectedModel, contextSize);
        session.Model = selectedModel;
        return $"Selected model: {selectedModel}";
    }

    private static string ExecuteChangeDirectoryCommand(AcpSession session, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return $"Working directory: {session.WorkingDirectory}\nUse /cd <path> to change it for this ACP session.";
        }

        string candidate = arguments.Trim().Trim('\"', '\'');
        string path = Path.IsPathFullyQualified(candidate)
            ? Path.GetFullPath(candidate)
            : Path.GetFullPath(candidate, session.WorkingDirectory);
        if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
        if (!Directory.Exists(path)) return $"Directory not found: {path}";

        session.WorkingDirectory = path;
        return $"Working directory: {path}";
    }

    private static async Task<string> ExecuteSideQuestionCommandAsync(AcpSession session, string arguments, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return "Usage: /ask <question>";
        ChatResponse response = await session.ChatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, Potato.Prompts.PromptLibrary.SideQuestionSystemPrompt),
                new ChatMessage(ChatRole.User, arguments)
            ],
            new ChatOptions(),
            cancellationToken);
        return string.IsNullOrWhiteSpace(response.Text) ? "No response was returned." : response.Text.Trim();
    }

    private string DescribeSessions()
    {
        AcpSession[] activeSessions = sessions.Values.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
        return activeSessions.Length == 0
            ? "No active ACP sessions."
            : string.Join("\n", activeSessions.Select(value => $"- {value.Id[..8]} · {value.Model} · {value.WorkingDirectory}"));
    }

    private static string DescribeTranscript(AcpSession session) =>
        session.History.Count == 0
            ? "This ACP session has no conversation history."
            : string.Join("\n\n", session.History.Select(message => $"{message.Role}: {message.Text}"));

    private static (string Command, string Arguments) SplitCommand(string input)
    {
        string trimmed = input.Trim();
        int separator = trimmed.IndexOf(' ');
        return separator < 0
            ? (trimmed.ToLowerInvariant(), string.Empty)
            : (trimmed[..separator].ToLowerInvariant(), trimmed[(separator + 1)..].Trim());
    }

    private Task NotifyAsync(string sessionId, string updateType, string text) =>
        WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "session/update",
            ["params"] = new JsonObject
            {
                ["sessionId"] = sessionId,
                ["update"] = new JsonObject
                {
                    ["sessionUpdate"] = updateType,
                    ["content"] = new JsonObject { ["type"] = "text", ["text"] = text }
                }
            }
        });

    private async Task ReplyErrorAsync(JsonNode? id, int code, string message)
    {
        if (id is null) return;
        await WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        });
    }

    private async Task WriteAsync(JsonObject message)
    {
        await outputLock.WaitAsync();
        try
        {
            await Console.Out.WriteLineAsync(message.ToJsonString());
            await Console.Out.FlushAsync();
        }
        finally
        {
            outputLock.Release();
        }
    }

    private static string GetRequiredString(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new AcpRequestException(-32602, $"'{name}' is required.");

    private static int GetInt(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : throw new AcpRequestException(-32602, $"'{name}' is required.");

    private static string GetPromptText(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("prompt", out JsonElement prompt) || prompt.ValueKind != JsonValueKind.Array)
            return string.Empty;

        return string.Join("\n", prompt.EnumerateArray()
            .Where(item => item.TryGetProperty("type", out JsonElement type) && type.GetString() == "text")
            .Select(item => item.TryGetProperty("text", out JsonElement text) ? text.GetString() : null)
            .Where(text => !string.IsNullOrWhiteSpace(text))!);
    }

    private sealed class AcpSession(string id, string workingDirectory, IChatClient chatClient, string model) : IDisposable
    {
        private readonly object cancellationLock = new();
        private CancellationTokenSource promptCancellation = new();
        public string Id { get; } = id;
        public string WorkingDirectory { get; set; } = workingDirectory;
        public IChatClient ChatClient { get; set; } = chatClient;
        public string Model { get; set; } = model;
        public List<ChatMessage> History { get; } = [];
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public CancellationToken StartPromptCancellation()
        {
            lock (cancellationLock)
            {
                promptCancellation.Dispose();
                promptCancellation = new CancellationTokenSource();
                return promptCancellation.Token;
            }
        }

        public void CancelPrompt()
        {
            lock (cancellationLock)
            {
                promptCancellation.Cancel();
            }
        }

        public void Dispose()
        {
            lock (cancellationLock)
            {
                promptCancellation.Cancel();
                promptCancellation.Dispose();
            }

            Gate.Dispose();
        }
    }

    private sealed class AcpRequestException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
