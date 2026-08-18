using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Potato.Session;
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
    int contextSize,
    ReActSession reActSession,
    PlanningService planningService)
{
    private const int ProtocolVersion = 1;
    private static readonly TimeSpan PermissionResponseTimeout = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, AcpSession> sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim outputLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pendingClientRequests = new(StringComparer.Ordinal);
    // ReAct and its tools currently resolve relative paths from Environment.CurrentDirectory
    // and share execution memory. Serialize ACP executions while that runtime contract exists.
    private readonly SemaphoreSlim reactGate = new(1, 1);
    private long nextClientRequestId;

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
            if (!request.TryGetProperty("method", out _) && request.TryGetProperty("id", out JsonElement responseId))
            {
                CompleteClientRequest(responseId, request);
                return;
            }

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
                ["loadSession"] = false,
                ["promptCapabilities"] = new JsonObject
                {
                    ["embeddedContext"] = true,
                    ["image"] = false,
                    ["audio"] = false
                }
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

        AcpPromptContext context = ParsePromptContext(parameters);
        if (string.IsNullOrWhiteSpace(context.UserText) && context.Attachments.Count == 0)
        {
            throw new AcpRequestException(-32602, "session/prompt requires text or supported embedded context.");
        }

        if (context.UserText.StartsWith("/", StringComparison.Ordinal) && context.Attachments.Count == 0)
        {
            string commandResponse = await ExecuteCommandAsync(session, context.UserText, serverCancellationToken);
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
            string contextSummary = context.Describe();
            webUiReporter.Record("message", "user", context.UserText, collapsed: false);
            await NotifyAsync(sessionId, "agent_thought_chunk", "Potato is processing the ACP prompt.");
            if (context.Attachments.Count > 0)
            {
                await NotifyAsync(sessionId, "agent_thought_chunk", contextSummary);
                webUiReporter.Record("status", "status", contextSummary, collapsed: true);
            }

            string executionDirectory = context.GetExecutionDirectory(session.WorkingDirectory);
            string goal = context.BuildModelMessage(executionDirectory);
            string text = await ExecuteReActAsync(session, goal, executionDirectory, linkedCancellation.Token);
            session.History.Add(new ChatMessage(ChatRole.User, context.UserText));
            session.History.Add(new ChatMessage(ChatRole.Assistant, text));
            webUiReporter.Record("message", "assistant", text, collapsed: false);
            await NotifyAsync(sessionId, "agent_message_chunk", text);

            return new JsonObject { ["stopReason"] = "end_turn" };
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task<string> ExecuteReActAsync(
        AcpSession session,
        string goal,
        string executionDirectory,
        CancellationToken cancellationToken)
    {
        await reactGate.WaitAsync(cancellationToken);
        string originalWorkingDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = executionDirectory;
            string guidance = planningService.BuildDirectExecutionGuidance(executionDirectory);
            using IDisposable permissionHandler = PotatoConsole.PushPermissionRequestHandler(
                (permissionKey, title, details, prompt) => RequestPermissionAsync(session, permissionKey, title, details, prompt, cancellationToken));
            using IDisposable activityHandler = PotatoConsole.PushActivityHandler(
                (kind, content) => PublishReActActivity(session.Id, kind, content));
            return await reActSession.ExecuteAsync(
                goal,
                guidance,
                session.ChatClient,
                cancellationToken,
                useNativeToolCalls: true,
                allowInteractiveUserIntervention: false);
        }
        finally
        {
            Environment.CurrentDirectory = originalWorkingDirectory;
            reactGate.Release();
        }
    }

    private void PublishReActActivity(string sessionId, string kind, string content)
    {
        _ = kind == "tool-call"
            ? NotifyToolCallAsync(sessionId, content)
            : NotifyAsync(sessionId, "agent_thought_chunk", content);
    }

    private Task NotifyToolCallAsync(string sessionId, string content) =>
        WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "session/update",
            ["params"] = new JsonObject
            {
                ["sessionId"] = sessionId,
                ["update"] = new JsonObject
                {
                    ["sessionUpdate"] = "tool_call",
                    ["toolCallId"] = Guid.NewGuid().ToString("N"),
                    ["title"] = content.Split('\n', 2)[0],
                    ["kind"] = "other",
                    ["status"] = "in_progress",
                    ["rawInput"] = content
                }
            }
        });

    private async Task<ToolPermissionChoice> RequestPermissionAsync(
        AcpSession session,
        string permissionKey,
        string title,
        IReadOnlyList<string> details,
        string prompt,
        CancellationToken cancellationToken)
    {
        if (session.AlwaysAllowedPermissionKeys.Contains(permissionKey))
        {
            return ToolPermissionChoice.AllowAlways;
        }

        string sessionId = session.Id;
        string requestId = Interlocked.Increment(ref nextClientRequestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingClientRequests.TryAdd(requestId, completion)) return ToolPermissionChoice.Deny;

        try
        {
            await NotifyAsync(sessionId, "agent_thought_chunk", "Potato is waiting for Rider to approve this tool call.");
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["method"] = "session/request_permission",
                ["params"] = new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["toolCall"] = new JsonObject
                    {
                        ["toolCallId"] = Guid.NewGuid().ToString("N"),
                        ["title"] = title,
                        ["kind"] = title.StartsWith("WriteFile", StringComparison.Ordinal) ? "edit" : "execute",
                        ["status"] = "pending",
                        ["rawInput"] = string.Join(Environment.NewLine, details)
                    },
                    ["options"] = new JsonArray
                    {
                        PermissionOption("once", "Allow once", "allow_once"),
                        PermissionOption("always", "Always allow", "allow_always"),
                        PermissionOption("deny", "Deny", "reject_once")
                    }
                }
            });

            try
            {
                JsonElement response = await completion.Task.WaitAsync(PermissionResponseTimeout, cancellationToken);
                ToolPermissionChoice choice = ParsePermissionChoice(response);
                if (choice == ToolPermissionChoice.AllowAlways)
                {
                    session.AlwaysAllowedPermissionKeys.Add(permissionKey);
                }

                return choice;
            }
            catch (TimeoutException)
            {
                await NotifyAsync(sessionId, "agent_thought_chunk", "Rider did not respond to the ACP permission request within two minutes; Potato denied the tool call to avoid waiting indefinitely.");
                return ToolPermissionChoice.Deny;
            }
        }
        finally
        {
            pendingClientRequests.TryRemove(requestId, out _);
        }
    }

    private static JsonObject PermissionOption(string optionId, string name, string kind) => new()
    {
        ["optionId"] = optionId,
        ["name"] = name,
        ["kind"] = kind
    };

    private void CompleteClientRequest(JsonElement id, JsonElement response)
    {
        string requestId = id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText();
        if (pendingClientRequests.TryGetValue(requestId, out TaskCompletionSource<JsonElement>? completion)) completion.TrySetResult(response.Clone());
    }

    private static ToolPermissionChoice ParsePermissionChoice(JsonElement response)
    {
        if (!response.TryGetProperty("result", out JsonElement result) ||
            !result.TryGetProperty("outcome", out JsonElement outcome) ||
            !outcome.TryGetProperty("outcome", out JsonElement outcomeKind) ||
            !string.Equals(outcomeKind.GetString(), "selected", StringComparison.Ordinal) ||
            !outcome.TryGetProperty("optionId", out JsonElement optionId)) return ToolPermissionChoice.Deny;

        return optionId.GetString() switch
        {
            "once" or "allow_once" => ToolPermissionChoice.AllowOnce,
            "always" or "allow_always" => ToolPermissionChoice.AllowAlways,
            _ => ToolPermissionChoice.Deny
        };
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

    private static AcpPromptContext ParsePromptContext(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("prompt", out JsonElement prompt) || prompt.ValueKind != JsonValueKind.Array)
            return AcpPromptContext.Empty;

        var textParts = new List<string>();
        var attachments = new List<AcpContextAttachment>();
        int remainingCharacters = AcpPromptContext.MaxAttachmentCharacters;

        foreach (JsonElement item in prompt.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out JsonElement type)) continue;
            switch (type.GetString())
            {
                case "text" when item.TryGetProperty("text", out JsonElement text):
                    if (!string.IsNullOrWhiteSpace(text.GetString())) textParts.Add(text.GetString()!);
                    break;
                case "resource":
                    AddEmbeddedResource(item, attachments, ref remainingCharacters);
                    break;
                case "resource_link":
                    AddResourceLink(item, attachments, ref remainingCharacters);
                    break;
            }
        }

        return new AcpPromptContext(string.Join("\n", textParts), attachments);
    }

    private static void AddEmbeddedResource(JsonElement item, ICollection<AcpContextAttachment> attachments, ref int remainingCharacters)
    {
        if (!item.TryGetProperty("resource", out JsonElement resource) || resource.ValueKind != JsonValueKind.Object) return;

        string uri = GetOptionalString(resource, "uri") ?? "embedded resource";
        string? text = GetOptionalString(resource, "text");
        if (string.IsNullOrEmpty(text))
        {
            attachments.Add(AcpContextAttachment.Unsupported(uri, "binary resource"));
            return;
        }

        int length = Math.Min(text.Length, Math.Max(0, remainingCharacters));
        bool truncated = length < text.Length;
        if (length > 0)
        {
            attachments.Add(AcpContextAttachment.Embedded(uri, text[..length], truncated));
            remainingCharacters -= length;
        }
        else
        {
            attachments.Add(AcpContextAttachment.Unsupported(uri, "context limit reached"));
        }
    }

    private static void AddResourceLink(JsonElement item, ICollection<AcpContextAttachment> attachments, ref int remainingCharacters)
    {
        string uriText = GetOptionalString(item, "uri") ?? "resource link";
        string? name = GetOptionalString(item, "name") ?? GetOptionalString(item, "title");

        // Rider sends an attached editor file as a resource_link rather than an inline
        // resource. A link alone gives ReAct no file contents to act on, causing it to
        // rediscover the file through the project map. Treat a locally attached file as
        // supplied context and keep the existing bounded-context contract.
        if (Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri) && uri.IsFile)
        {
            try
            {
                string filePath = uri.LocalPath;
                if (!File.Exists(filePath))
                {
                    attachments.Add(AcpContextAttachment.Unsupported(uriText, "linked local file was not found"));
                    return;
                }

                int capacity = Math.Max(0, remainingCharacters);
                if (capacity == 0)
                {
                    attachments.Add(AcpContextAttachment.Unsupported(uriText, "context limit reached"));
                    return;
                }

                using var reader = new StreamReader(filePath);
                char[] buffer = new char[capacity + 1];
                int read = reader.ReadBlock(buffer, 0, buffer.Length);
                bool truncated = read > capacity;
                string content = new(buffer, 0, Math.Min(read, capacity));
                attachments.Add(AcpContextAttachment.Embedded(uriText, content, truncated));
                remainingCharacters -= content.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                attachments.Add(AcpContextAttachment.Unsupported(uriText, "linked local file could not be read"));
            }

            return;
        }

        attachments.Add(AcpContextAttachment.Link(uriText, name));
    }

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private sealed class AcpSession(string id, string workingDirectory, IChatClient chatClient, string model) : IDisposable
    {
        private readonly object cancellationLock = new();
        private CancellationTokenSource promptCancellation = new();
        public string Id { get; } = id;
        public string WorkingDirectory { get; set; } = workingDirectory;
        public IChatClient ChatClient { get; set; } = chatClient;
        public string Model { get; set; } = model;
        public List<ChatMessage> History { get; } = [];
        public HashSet<string> AlwaysAllowedPermissionKeys { get; } = new(StringComparer.Ordinal);
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

    private sealed class AcpPromptContext(string userText, IReadOnlyList<AcpContextAttachment> attachments)
    {
        public const int MaxAttachmentCharacters = 48_000;
        public static AcpPromptContext Empty { get; } = new(string.Empty, []);
        public string UserText { get; } = userText;
        public IReadOnlyList<AcpContextAttachment> Attachments { get; } = attachments;

        public string GetExecutionDirectory(string fallbackDirectory)
        {
            foreach (AcpContextAttachment attachment in Attachments)
            {
                if (attachment.Kind != AcpContextAttachmentKind.Embedded ||
                    !Uri.TryCreate(attachment.Uri, UriKind.Absolute, out Uri? uri) ||
                    !uri.IsFile)
                {
                    continue;
                }

                string? projectDirectory = FindProjectDirectory(Path.GetDirectoryName(uri.LocalPath));
                if (projectDirectory is not null)
                {
                    return projectDirectory;
                }
            }

            return fallbackDirectory;
        }

        public string Describe()
        {
            int embedded = Attachments.Count(value => value.Kind == AcpContextAttachmentKind.Embedded);
            int links = Attachments.Count(value => value.Kind == AcpContextAttachmentKind.Link);
            int skipped = Attachments.Count(value => value.Kind == AcpContextAttachmentKind.Unsupported);
            var parts = new List<string>();
            if (embedded > 0) parts.Add($"{embedded} embedded attachment{(embedded == 1 ? string.Empty : "s")}");
            if (links > 0) parts.Add($"{links} resource link{(links == 1 ? string.Empty : "s")}");
            if (skipped > 0) parts.Add($"{skipped} unsupported attachment{(skipped == 1 ? string.Empty : "s")}");
            return parts.Count == 0 ? "No IDE context was attached." : $"IDE context received: {string.Join(", ", parts)}.";
        }

        public string BuildModelMessage(string workingDirectory)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine($"ACP workspace: {workingDirectory}");
            builder.AppendLine();
            builder.AppendLine("User request:");
            builder.AppendLine(string.IsNullOrWhiteSpace(UserText) ? "Use the attached IDE context." : UserText);

            if (Attachments.Any(value => value.Kind == AcpContextAttachmentKind.Embedded))
            {
                builder.AppendLine();
                builder.AppendLine("Attached local files are authoritative, current source context. Treat each as a confirmed target: use its supplied contents to make the requested change and do not search the project map or call ReadFileContent merely to rediscover it. A complete attachment satisfies the required initial file read; only re-read an attachment if it is marked truncated or after writing to verify the result. Relative output paths are relative to the attached file's project directory shown above.");
            }

            foreach (AcpContextAttachment attachment in Attachments)
            {
                builder.AppendLine();
                builder.AppendLine($"IDE context: {attachment.Uri}");
                if (attachment.Kind == AcpContextAttachmentKind.Embedded)
                {
                    builder.AppendLine("```text");
                    builder.AppendLine(attachment.Content);
                    builder.AppendLine("```");
                    if (attachment.Truncated) builder.AppendLine("[Attachment truncated to fit Potato's ACP context limit.]");
                }
                else
                {
                    builder.AppendLine($"[{attachment.Content}]");
                }
            }

            return builder.ToString().TrimEnd();
        }

        private static string? FindProjectDirectory(string? startingDirectory)
        {
            for (string? directory = startingDirectory; !string.IsNullOrEmpty(directory); directory = Directory.GetParent(directory)?.FullName)
            {
                try
                {
                    if (Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Any() ||
                        Directory.EnumerateFiles(directory, "package.json", SearchOption.TopDirectoryOnly).Any())
                    {
                        return directory;
                    }
                }
                catch (IOException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
            }

            return null;
        }
    }

    private enum AcpContextAttachmentKind { Embedded, Link, Unsupported }

    private sealed record AcpContextAttachment(AcpContextAttachmentKind Kind, string Uri, string Content, bool Truncated)
    {
        public static AcpContextAttachment Embedded(string uri, string content, bool truncated) => new(AcpContextAttachmentKind.Embedded, uri, content, truncated);
        public static AcpContextAttachment Link(string uri, string? name) => new(AcpContextAttachmentKind.Link, uri, name ?? "Referenced resource; content was not embedded.", false);
        public static AcpContextAttachment Unsupported(string uri, string reason) => new(AcpContextAttachmentKind.Unsupported, uri, $"Not included: {reason}.", false);
    }
}
