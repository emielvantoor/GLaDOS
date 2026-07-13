using System.Net.Http.Json;

namespace Potato.WebUi;

internal sealed class PotatoWebUiReporter(Uri gladosEndpoint, string model) : PotatoConsole.IPotatoConsoleEventSink, IAsyncDisposable
{
    private readonly HttpClient httpClient = new();
    private readonly string workingDirectory = Environment.CurrentDirectory;
    private readonly Uri startSessionUri = new(gladosEndpoint, "potato/sessions");
    private readonly Uri eventUri = new(gladosEndpoint, "potato/sessions/events");
    private volatile bool disabled;

    public async Task StartAsync()
    {
        await TryPostAsync(startSessionUri, new PotatoSessionStartPayload(
            workingDirectory,
            model,
            Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
    }

    public void Record(string kind, string role, string content, bool collapsed)
    {
        if (disabled || string.IsNullOrWhiteSpace(content))
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

    public async ValueTask DisposeAsync()
    {
        if (!disabled)
        {
            await TryPostAsync(eventUri, new PotatoSessionEventPayload(
                workingDirectory,
                "stopped",
                "status",
                "Potato session stopped.",
                Collapsed: true));
        }

        httpClient.Dispose();
    }

    private async Task TryPostAsync<TPayload>(Uri uri, TPayload payload)
    {
        if (disabled)
        {
            return;
        }

        try
        {
            using HttpResponseMessage response = await httpClient.PostAsJsonAsync(uri, payload);
            if (!response.IsSuccessStatusCode)
            {
                disabled = true;
            }
        }
        catch
        {
            disabled = true;
        }
    }

    private sealed record PotatoSessionStartPayload(string WorkingDirectory, string Model, string? DisplayName);

    private sealed record PotatoSessionEventPayload(
        string WorkingDirectory,
        string Kind,
        string Role,
        string Content,
        bool Collapsed);
}
