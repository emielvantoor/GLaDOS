using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Potato;

internal sealed class GladosChatClientFactory
{
    public IChatClient CreateOpenAiClient(Uri gladosEndpoint, string model)
    {
        IChatClient openAiClient = new ChatClient(
            model,
            new ApiKeyCredential("glados-local"),
            new OpenAIClientOptions
            {
                Endpoint = gladosEndpoint
            }).AsIChatClient();

        return new ChatClientBuilder(openAiClient)
            .UseFunctionInvocation()
            .Build();
    }

}
