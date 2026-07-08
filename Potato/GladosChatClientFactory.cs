using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

internal sealed class GladosChatClientFactory
{
    public IChatClient CreateOpenAiClient(Uri gladosEndpoint, string model)
    {
        return new ChatClient(
            model,
            new ApiKeyCredential("glados-local"),
            new OpenAIClientOptions
            {
                Endpoint = gladosEndpoint
            }).AsIChatClient();
    }

}
