using Microsoft.Extensions.AI;

namespace Potato;

public sealed class CurrentChatClientState
{
    private readonly object syncRoot = new();
    private IChatClient? openAiClient;

    public IChatClient OpenAiClient
    {
        get
        {
            lock (syncRoot)
            {
                return openAiClient ?? throw new InvalidOperationException("No OpenAI chat client has been selected.");
            }
        }
    }

    public void SetOpenAiClient(IChatClient selectedOpenAiClient)
    {
        lock (syncRoot)
        {
            openAiClient = selectedOpenAiClient;
        }
    }
}