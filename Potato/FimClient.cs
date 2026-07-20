using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Potato;

public sealed class FimClient
{
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly Uri endpoint = GladosConfiguration.GetEndpoint();
    private bool? available;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (available is { } cached)
        {
            return cached;
        }

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(new Uri(endpoint, "fim/capabilities"), cancellationToken);
            available = response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            available = false;
        }

        return available.Value;
    }

    public async Task<string> GenerateAsync(string model, string prefix, string suffix, int maxCompletionTokens, CancellationToken cancellationToken)
    {
        var request = new
        {
            model,
            prefix,
            suffix,
            stream = false,
            temperature = 0.0f,
            max_completion_tokens = maxCompletionTokens
        };
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(new Uri(endpoint, "fim/completions"), request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                available = false;
            }

            return $"Error: FIM generation failed with HTTP {(int)response.StatusCode}.";
        }

        using JsonDocument document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("FIM returned an empty response.");
        return document.RootElement.GetProperty("choices")[0].GetProperty("text").GetString() ?? string.Empty;
    }
}
