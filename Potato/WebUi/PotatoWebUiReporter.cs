using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;

namespace Potato.WebUi;

internal sealed class PotatoWebUiReporter(Uri gladosEndpoint, string model) : PotatoConsole.IPotatoConsoleEventSink, IAsyncDisposable
{
    private readonly HttpClient httpClient = new();
    private readonly Channel<string> inputChannel = Channel.CreateUnbounded<string>();
    private readonly string workingDirectory = Environment.CurrentDirectory;
    private readonly Uri startSessionUri = new(gladosEndpoint, "potato/sessions");
    private readonly Uri eventUri = new(gladosEndpoint, "potato/sessions/events");
    private readonly Uri nextInputUri = new(gladosEndpoint, $"potato/sessions/input/next?workingDirectory={Uri.EscapeDataString(Environment.CurrentDirectory)}");
    private readonly CancellationTokenSource inputPollingCancellation = new();
    private Task? inputPollingTask;

    public async Task StartAsync()
    {
        await TryPostAsync(startSessionUri, new PotatoSessionStartPayload(
            workingDirectory,
            model,
            Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
        inputPollingTask = PollInputAsync(inputPollingCancellation.Token);
    }

    public void Record(string kind, string role, string content, bool collapsed)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        _ = Task.Run(() => TryPostAsync(eventUri, new PotatoSessionEventPayload(
            workingDirectory,
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
            workingDirectory,
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
                using HttpResponseMessage response = await httpClient.GetAsync(nextInputUri, cancellationToken);
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

    private sealed record PotatoSessionStartPayload(string WorkingDirectory, string Model, string? DisplayName);

    private sealed record PotatoSessionEventPayload(
        string WorkingDirectory,
        string Kind,
        string Role,
        string Content,
        bool Collapsed);

    private sealed record InputPayload(string Content);
}
