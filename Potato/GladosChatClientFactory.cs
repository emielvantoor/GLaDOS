using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Potato.WebUi;

namespace Potato;

internal sealed class GladosChatClientFactory
{
    public IChatClient CreateOpenAiClient(Uri gladosEndpoint, string model, int contextSize)
    {
        IChatClient openAiClient = new ChatClient(
            model,
            new ApiKeyCredential("glados-local"),
            new OpenAIClientOptions
            {
                Endpoint = gladosEndpoint
            }).AsIChatClient();

        return new PotatoModelCommunicationLogger(openAiClient, contextSize);
    }

}
