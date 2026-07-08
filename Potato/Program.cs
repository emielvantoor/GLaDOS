using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Potato;
using Potato.Session;
using Potato.Session.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        Console.Title = "Potato Code";
        var appSettingsStore = new PotatoAppSettingsStore(PotatoAppSettingsStore.DefaultPath);
        PotatoAppSettings appSettings = appSettingsStore.Load();
        PotatoRuntimeOptions options = PotatoRuntimeOptions.FromArgs(args, appSettings);
        PromptLibrary.Configure(options.PromptDirectory, options.UseCompiledDefaultPrompts);

        var services = new ServiceCollection();

        services.AddSingleton(options);
        services.AddSingleton<CurrentChatClientState>();
        services.AddSingleton<ExecutionService>();
        services.AddSingleton<PlanningService>();
        services.AddSingleton<AgentTools>();
        services.AddSingleton<ExecutionMemory>();

        services.AddSingleton<IAgentTask, CodeReviewTask>();
        services.AddSingleton<IAgentTask, CreateNewFileTask>();
        services.AddSingleton<IAgentTask, ReadFileTask>();
        services.AddSingleton<IAgentTask, WriteReportTask>();
        services.AddSingleton<IAgentTask, RefactorTask>();

        var provider = services.BuildServiceProvider();

        Uri gladosEndpoint = GladosConfiguration.GetEndpoint();
        var clientFactory = new GladosChatClientFactory();
        var modelSelector = new ModelSelector();
        string model = await modelSelector.SelectStartupModelAsync(gladosEndpoint, appSettings.SelectedModel);
        appSettingsStore.SetSelectedModel(model);

        IChatClient openAiClient = clientFactory.CreateOpenAiClient(gladosEndpoint, model);
        provider.GetRequiredService<CurrentChatClientState>().SetOpenAiClient(openAiClient);

        PotatoConsole.WriteStartupBanner(gladosEndpoint, model);


        var session = new PotatoSession(
            gladosEndpoint,
            openAiClient,
            clientFactory,
            modelSelector,
            options,
            appSettingsStore,
            provider.GetRequiredService<AgentTools>(),
            provider.GetRequiredService<ExecutionMemory>(),
            provider.GetRequiredService<CurrentChatClientState>(),
            provider.GetRequiredService<PlanningService>(),
            provider.GetRequiredService<ExecutionService>()
        );

        await session.RunAsync();
    }
}
