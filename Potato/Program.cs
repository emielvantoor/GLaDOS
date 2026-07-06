using Microsoft.Extensions.AI;

class Program
{
    static async Task Main(string[] args)
    {
        Console.Title = "Potato Code";
        var appSettingsStore = new PotatoAppSettingsStore(PotatoAppSettingsStore.DefaultPath);
        PotatoAppSettings appSettings = appSettingsStore.Load();
        PotatoRuntimeOptions options = PotatoRuntimeOptions.FromArgs(args, appSettings);
        PromptLibrary.Configure(options.PromptDirectory, options.UseCompiledDefaultPrompts);

        Uri gladosEndpoint = GladosConfiguration.GetEndpoint();
        var clientFactory = new GladosChatClientFactory();
        var modelSelector = new ModelSelector();
        string model = await modelSelector.SelectStartupModelAsync(gladosEndpoint, appSettings.SelectedModel);
        appSettingsStore.SetSelectedModel(model);

        IChatClient openAiClient = clientFactory.CreateOpenAiClient(gladosEndpoint, model);
        IChatClient client = clientFactory.CreateFunctionClient(openAiClient);

        PotatoConsole.WriteStartupBanner(gladosEndpoint, model);

        var session = new PotatoSession(
            gladosEndpoint,
            openAiClient,
            client,
            clientFactory,
            modelSelector,
            options,
            appSettingsStore);

        await session.RunAsync();
    }
}
