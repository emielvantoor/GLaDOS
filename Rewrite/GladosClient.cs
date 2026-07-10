using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rewrite;

internal sealed class GladosClient(HttpClient httpClient, string model)
{
    public static GladosClient FromEnvironment()
    {
        string endpoint = Environment.GetEnvironmentVariable("GLADOS_OPENAI_ENDPOINT") ?? "http://localhost:11434/v1";
        string model = Environment.GetEnvironmentVariable("GLADOS_MODEL") ?? "Qwen3-8B-Q4_K_M";
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/")
        };

        return new GladosClient(httpClient, model);
    }

    public async Task<bool> CanUnderstandAsync(string wise)
    {
        string response = await CompleteAsync(
            "Answer only yes or no. Can this natural language request be translated into one safe shell command?",
            wise);
        return response.Trim().StartsWith("yes", StringComparison.OrdinalIgnoreCase);
    }

    public Task<string> GenerateShellCommandAsync(string wise) =>
        CompleteAsync(
            "Translate the user request into exactly one POSIX shell command. Return only the command, with no markdown or explanation.",
            wise);

    private async Task<string> CompleteAsync(string systemPrompt, string userPrompt)
    {
        var request = new ChatCompletionRequest(
            model,
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt)
            ],
            0.0f);

        using HttpResponseMessage response = await httpClient.PostAsJsonAsync("chat/completions", request);
        response.EnsureSuccessStatusCode();

        ChatCompletionResponse? completion = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>();
        return completion?.Choices.FirstOrDefault()?.Message.Content?.Trim() ?? string.Empty;
    }

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("temperature")] float Temperature);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice> Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage Message);
}
