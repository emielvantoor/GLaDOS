using Microsoft.Extensions.AI;

namespace Potato;

public sealed class CurrentChatClientState
{
    private readonly object syncRoot = new();
    private IChatClient? openAiClient;
    private string model = "local-model";

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

    public string Model
    {
        get { lock (syncRoot) { return model; } }
    }

    public void SetModel(string selectedModel)
    {
        lock (syncRoot) { model = selectedModel; }
    }
}
