using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Potato.Session;
using Potato.Session.Tasks;
using Potato.Tools;
using Potato.WebUi;

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
        services.AddSingleton<ProjectMapBuilder>();
        services.AddSingleton<PlanningArtifactGenerator>();
        services.AddSingleton<PlanTaskNormalizer>();
        services.AddSingleton<PlannerTaskGenerator>();
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
        services.AddSingleton<IAgentTask, SearchProjectMapTask>();
        services.AddSingleton<IAgentTask, ShellScriptTask>();

        var provider = services.BuildServiceProvider();

        Uri gladosEndpoint = GladosConfiguration.GetEndpoint();
        var clientFactory = new GladosChatClientFactory();
        var modelSelector = new ModelSelector();
        string model = await modelSelector.SelectStartupModelAsync(gladosEndpoint, appSettings.SelectedModel);
        appSettingsStore.SetSelectedModel(model);

        IChatClient openAiClient = clientFactory.CreateOpenAiClient(gladosEndpoint, model);
        provider.GetRequiredService<CurrentChatClientState>().SetOpenAiClient(openAiClient);

        await using var webUiReporter = new PotatoWebUiReporter(gladosEndpoint, model);
        bool allowWebUiInput = string.Equals(
            Environment.GetEnvironmentVariable("POTATO_WEBUI_ALLOW_INPUT"),
            "true",
            StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Environment.GetEnvironmentVariable("POTATO_WEBUI_ALLOW_INPUT"),
                "1",
                StringComparison.OrdinalIgnoreCase);
        await webUiReporter.StartAsync(allowWebUiInput);
        PotatoConsole.EventSink = webUiReporter;

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

        try
        {
            await session.RunAsync();
        }
        finally
        {
            PotatoConsole.EventSink = null;
        }
    }
}
