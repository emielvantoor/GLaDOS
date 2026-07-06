using Microsoft.Extensions.AI;

class Program
{
    static async Task Main(string[] args)
    {
        Console.Title = "Potato Code";
        PotatoRuntimeOptions options = PotatoRuntimeOptions.FromArgs(args);

        Uri gladosEndpoint = GladosConfiguration.GetEndpoint();
        var clientFactory = new GladosChatClientFactory();
        var modelSelector = new ModelSelector();
        string model = await modelSelector.PromptForModelAsync(gladosEndpoint);

        IChatClient openAiClient = clientFactory.CreateOpenAiClient(gladosEndpoint, model);
        IChatClient client = clientFactory.CreateFunctionClient(openAiClient);

        PotatoConsole.WriteStartupBanner(gladosEndpoint, model);

        var session = new PotatoSession(
            gladosEndpoint,
            openAiClient,
            client,
            clientFactory,
            modelSelector,
            options);

        await session.RunAsync();
    }
}
