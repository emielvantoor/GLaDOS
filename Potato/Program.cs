using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Potato.Session;
using Potato.Session.Tasks;
using Potato.Tools;

namespace Potato;

class Program
{
    static async Task Main(string[] args)
    {
        Console.Title = "Potato Code";
        var appSettingsStore = new PotatoAppSettingsStore(PotatoAppSettingsStore.DefaultPath);
        PotatoAppSettings appSettings = appSettingsStore.Load();
        PotatoRuntimeOptions options = PotatoRuntimeOptions.FromArgs(args, appSettings);
        Potato.Prompts.PromptLibrary.Configure(options.PromptDirectory, options.UseCompiledDefaultPrompts);

        var services = new ServiceCollection();

        services.AddSingleton(options);
        services.AddSingleton<CurrentChatClientState>();
        services.AddSingleton<ExecutionService>();
        services.AddSingleton<PlanningService>();
        services.AddSingleton<ReActSession>();
        services.AddSingleton<AgentTools>();
        services.AddSingleton<ExecutionMemory>();

        services.AddSingleton<IAgentTask, CodeReviewTask>();
        services.AddSingleton<IAgentTask, CreateNewFileTask>();
        services.AddSingleton<IAgentTask, InspectProjectTask>();
        services.AddSingleton<IAgentTask, ReadFileTask>();
        services.AddSingleton<IAgentTask, WriteCodeTask>();
        services.AddSingleton<IAgentTask, WriteDocumentationTask>();
        services.AddSingleton<IAgentTask, WriteReportTask>();
        services.AddSingleton<IAgentTask, ApplyPatchTask>();
        services.AddSingleton<IAgentTask, ArchitectRefactorTask>();
        services.AddSingleton<IAgentTask, DesignTask>();
        services.AddSingleton<IAgentTask, ShellScriptTask>();

        var provider = services.BuildServiceProvider();

        Uri gladosEndpoint = GladosConfiguration.GetEndpoint();
        var clientFactory = new GladosChatClientFactory();
        var modelSelector = new ModelSelector();
        string model = await modelSelector.SelectStartupModelAsync(gladosEndpoint, appSettings.SelectedModel);
        appSettingsStore.SetSelectedModel(model);

        IChatClient openAiClient = clientFactory.CreateOpenAiClient(gladosEndpoint, model);
        provider.GetRequiredService<CurrentChatClientState>().SetOpenAiClient(openAiClient);

        PotatoConsole.WriteStartupBanner(gladosEndpoint, model);


        var session = new PipelineSession(
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
            provider.GetRequiredService<ExecutionService>(),
            provider.GetRequiredService<ReActSession>()
        );

        await session.RunAsync();
    }
}
