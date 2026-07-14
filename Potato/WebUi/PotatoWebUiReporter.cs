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

    public async Task StartAsync(bool allowInput = false)
    {
        await TryPostAsync(startSessionUri, new PotatoSessionStartPayload(
            sessionId,
            sessionWorkingDirectory,
            model,
            allowInput ? "input-enabled" : "observe-only",
            Path.GetFileName(sessionWorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
        if (allowInput)
        {
            Record("status", "status", "Web UI input is enabled for this Potato session. CLI input remains authoritative.", collapsed: true);
            inputPollingTask = PollInputAsync(inputPollingCancellation.Token);
        }
        else
        {
            Record("status", "status", "Web UI is observe-only for this Potato session.", collapsed: true);
        }
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

    public bool TryReadInput(out string? input) =>
        inputChannel.Reader.TryRead(out input);

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

        await TryPostAsync(eventUri, new PotatoSessionEventPayload(
            sessionId,
            sessionWorkingDirectory,
            Environment.CurrentDirectory,
            "stopped",
            "status",
            "Potato session stopped.",
            Collapsed: true));

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
        bool Collapsed);

    private sealed record InputPayload(string Content);
}
