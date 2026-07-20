using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Potato.WebUi;

namespace Potato;

internal sealed class GladosChatClientFactory(ExecutionMemory executionMemory, string sessionId) : IDisposable
{
    private static readonly TimeSpan ModelRequestTimeout = TimeSpan.FromMinutes(30);
    private readonly HttpClient httpClient = CreateSessionHttpClient(sessionId);

    public IChatClient CreateOpenAiClient(Uri gladosEndpoint, string model, int contextSize)
    {
        IChatClient openAiClient = new ChatClient(
            model,
            new ApiKeyCredential("glados-local"),
            new OpenAIClientOptions
            {
                Endpoint = gladosEndpoint,
                NetworkTimeout = ModelRequestTimeout,
                Transport = new HttpClientPipelineTransport(httpClient)
            }).AsIChatClient();

        return new PotatoModelCommunicationLogger(openAiClient, contextSize, executionMemory);
    }

    public void Dispose() => httpClient.Dispose();

    private static HttpClient CreateSessionHttpClient(string sessionId)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-GLaDOS-Session-Id", sessionId);
        return client;
    }

}
