using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Potato.WebUi;

namespace Potato;

internal sealed class GladosChatClientFactory
{
    private static readonly TimeSpan ModelRequestTimeout = TimeSpan.FromMinutes(30);

    public IChatClient CreateOpenAiClient(Uri gladosEndpoint, string model, int contextSize)
    {
        IChatClient openAiClient = new ChatClient(
            model,
            new ApiKeyCredential("glados-local"),
            new OpenAIClientOptions
            {
                Endpoint = gladosEndpoint,
                NetworkTimeout = ModelRequestTimeout
            }).AsIChatClient();

        return new PotatoModelCommunicationLogger(openAiClient, contextSize);
    }

}
