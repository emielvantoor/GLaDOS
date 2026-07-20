using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;

namespace Potato.WebUi;

internal sealed class PotatoWebUiReporter(Uri gladosEndpoint, string model) : PotatoConsole.IPotatoConsoleEventSink, IAsyncDisposable
{
    private readonly HttpClient httpClient = new();
    private readonly Channel<string> inputChannel = Channel.CreateUnbounded<string>();
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private readonly string sessionWorkingDirectory = Environment.CurrentDirectory;
    private readonly Uri startSessionUri = new(gladosEndpoint, "potato/sessions");
    private readonly Uri eventUri = new(gladosEndpoint, "potato/sessions/events");
    private readonly CancellationTokenSource inputPollingCancellation = new();
    private Task? inputPollingTask;
    private Task? heartbeatTask;
    private volatile bool allowInput;

    public string SessionId => sessionId;

    public async Task StartAsync(bool allowInput = false)
    {
        this.allowInput = allowInput;
        await TryPostAsync(startSessionUri, new PotatoSessionStartPayload(
            sessionId,
            sessionWorkingDirectory,
            model,
            allowInput ? "input-enabled" : "observe-only",
            Path.GetFileName(sessionWorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
        if (allowInput)
        {
            Record("status", "status", "Web UI input is enabled for this Potato session. CLI input remains authoritative.", collapsed: true);
        }
        else
        {
            Record("status", "status", "Web UI is observe-only for this Potato session.", collapsed: true);
        }

        inputPollingTask = PollInputAsync(inputPollingCancellation.Token);
        heartbeatTask = SendHeartbeatsAsync(inputPollingCancellation.Token);
    }

    public void Record(string kind, string role, string content, bool collapsed)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        string currentWorkingDirectory = Environment.CurrentDirectory;
        _ = Task.Run(() => TryPostAsync(eventUri, new PotatoSessionEventPayload(
            sessionId,
            sessionWorkingDirectory,
            currentWorkingDirectory,
            kind,
            role,
            content,
            collapsed)));
    }

    public void RecordContextUsage(
        int promptTokens,
        int contextSize,
        double percentage,
        int maxOutputTokens,
        int headroomAfterReservedOutput,
        bool exceedsContext,
        string summary)
    {
        string content = string.IsNullOrWhiteSpace(summary)
            ? $"{promptTokens:N0}/{contextSize:N0} {percentage:0.#}%"
            : summary;

        string currentWorkingDirectory = Environment.CurrentDirectory;
        _ = Task.Run(() => TryPostAsync(eventUri, new PotatoSessionEventPayload(
            sessionId,
            sessionWorkingDirectory,
            currentWorkingDirectory,
            "context-usage",
            "status",
            content,
            Collapsed: true,
            ContextUsage: new PotatoContextUsagePayload(
                promptTokens,
                contextSize,
                percentage,
                maxOutputTokens,
                headroomAfterReservedOutput,
                exceedsContext,
                content))));
    }

    public bool TryReadInput(out string? input)
    {
        return inputChannel.Reader.TryRead(out input);
    }

    public async Task SetWebUiInputEnabledAsync(bool enabled)
    {
        allowInput = enabled;
        if (!enabled)
        {
            while (inputChannel.Reader.TryRead(out _))
            {
            }
        }

        await TryPostAsync(eventUri, new PotatoSessionEventPayload(
            sessionId,
            sessionWorkingDirectory,
            Environment.CurrentDirectory,
            enabled ? "webui-input-enabled" : "webui-input-disabled",
            "status",
            enabled
                ? "Web UI input is enabled for this Potato session."
                : "Web UI input is disabled for this Potato session.",
            Collapsed: true));
    }

    public async ValueTask DisposeAsync()
    {
        await inputPollingCancellation.CancelAsync();
        if (inputPollingTask is not null)
        {
            try
            {
                await inputPollingTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (heartbeatTask is not null)
        {
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await TryPostAsync(eventUri, new PotatoSessionEventPayload(
            sessionId,
            sessionWorkingDirectory,
            Environment.CurrentDirectory,
            "stopped",
            "status",
            "Potato session stopped.",
            Collapsed: true));

        await TryReleaseInferenceSessionAsync();

        inputPollingCancellation.Dispose();
        httpClient.Dispose();
    }

    private async Task PollInputAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using HttpResponseMessage response = await httpClient.GetAsync(BuildNextInputUri(), cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    InputPayload? payload = await response.Content.ReadFromJsonAsync<InputPayload>(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(payload?.Content))
                    {
                        await inputChannel.Writer.WriteAsync(payload.Content, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                await Task.Delay(1000, cancellationToken);
                continue;
            }

            await Task.Delay(200, cancellationToken);
        }
    }

    private async Task SendHeartbeatsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                using HttpResponseMessage _ = await httpClient.PostAsync(
                    new Uri(gladosEndpoint, $"v1/runtime/sessions/{Uri.EscapeDataString(sessionId)}/heartbeat"),
                    content: null,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // The server watchdog will release the context if it stays unreachable.
            }
        }
    }

    private async Task TryPostAsync<TPayload>(Uri uri, TPayload payload)
    {
        try
        {
            using HttpResponseMessage _ = await httpClient.PostAsJsonAsync(uri, payload);
        }
        catch
        {
            // Transient Web UI disconnects should not permanently detach this Potato session.
        }
    }

    private async Task TryReleaseInferenceSessionAsync()
    {
        try
        {
            using HttpResponseMessage _ = await httpClient.DeleteAsync(
                new Uri(gladosEndpoint, $"v1/runtime/sessions/{Uri.EscapeDataString(sessionId)}"));
        }
        catch
        {
            // GLaDOS may already be offline during Potato shutdown.
        }
    }

    private Uri BuildNextInputUri() =>
        new(
            gladosEndpoint,
            $"potato/sessions/input/next?workingDirectory={Uri.EscapeDataString(Environment.CurrentDirectory)}&sessionId={Uri.EscapeDataString(sessionId)}");

    private sealed record PotatoSessionStartPayload(string SessionId, string WorkingDirectory, string Model, string Mode, string? DisplayName);

    private sealed record PotatoSessionEventPayload(
        string SessionId,
        string WorkingDirectory,
        string CurrentWorkingDirectory,
        string Kind,
        string Role,
        string Content,
        bool Collapsed,
        PotatoContextUsagePayload? ContextUsage = null);

    private sealed record PotatoContextUsagePayload(
        int PromptTokens,
        int ContextSize,
        double Percentage,
        int MaxOutputTokens,
        int HeadroomAfterReservedOutput,
        bool ExceedsContext,
        string Summary);

    private sealed record InputPayload(string Content);
}
