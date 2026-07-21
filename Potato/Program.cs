using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Potato.Session;
using Potato.Tools;
using Potato.WebUi;
using Potato.Acp;

namespace Potato;

class Program
{
    static async Task Main(string[] args)
    {
        Console.Title = "Potato Code";
        InitializeWorkingDirectory();
        var appSettingsStore = new PotatoAppSettingsStore(PotatoAppSettingsStore.DefaultPath);
        PotatoAppSettings appSettings = appSettingsStore.Load();
        PotatoRuntimeOptions options = PotatoRuntimeOptions.FromArgs(args, appSettings);
        Potato.Prompts.PromptLibrary.Configure(options.PromptDirectory, options.UseCompiledDefaultPrompts);

        var services = new ServiceCollection();

        services.AddSingleton(options);
        services.AddSingleton<CurrentChatClientState>();
        services.AddSingleton<FimClient>();
        services.AddSingleton<ProjectMapBuilder>();
        services.AddSingleton<PlanningService>();
        services.AddSingleton<ContextCompactor>();
        services.AddSingleton<ReActSession>();
        services.AddSingleton<AgentTools>();
        services.AddSingleton<ExecutionMemory>();

        var provider = services.BuildServiceProvider();

        Uri gladosEndpoint = GladosConfiguration.GetEndpoint();
        var executionMemory = provider.GetRequiredService<ExecutionMemory>();
        var modelSelector = new ModelSelector();
        string model = options.AcpMode
            ? await modelSelector.SelectAcpModelAsync(gladosEndpoint, options.Model)
            : await modelSelector.SelectStartupModelAsync(gladosEndpoint, appSettings.SelectedModel);
        appSettingsStore.SetSelectedModel(model);

        await using var webUiReporter = new PotatoWebUiReporter(gladosEndpoint, model);
        using var clientFactory = new GladosChatClientFactory(executionMemory, webUiReporter.SessionId);
        IChatClient openAiClient = clientFactory.CreateOpenAiClient(gladosEndpoint, model, options.ContextSize);
        provider.GetRequiredService<CurrentChatClientState>().SetOpenAiClient(openAiClient);
        provider.GetRequiredService<CurrentChatClientState>().SetModel(model);
        await webUiReporter.StartAsync(options.WebUiInputEnabled);
        PotatoConsole.EventSink = webUiReporter;

        if (options.AcpMode)
        {
            try
            {
                var acpServer = new AcpAgentServer(
                    openAiClient,
                    model,
                    webUiReporter,
                    gladosEndpoint,
                    clientFactory,
                    options.ContextSize,
                    provider.GetRequiredService<ReActSession>(),
                    provider.GetRequiredService<PlanningService>());
                await acpServer.RunAsync(CancellationToken.None);
            }
            finally
            {
                PotatoConsole.EventSink = null;
            }

            return;
        }

        PotatoConsole.WriteStartupBanner(gladosEndpoint, model);

        var session = new PotatoSession(
            gladosEndpoint,
            openAiClient,
            clientFactory,
            modelSelector,
            options,
            appSettingsStore,
            provider.GetRequiredService<AgentTools>(),
            executionMemory,
            provider.GetRequiredService<CurrentChatClientState>(),
            provider.GetRequiredService<PlanningService>(),
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

    private static void InitializeWorkingDirectory()
    {
        string currentDirectory = Environment.CurrentDirectory;
        string appBaseDirectory = AppContext.BaseDirectory;
        if (!IsSameOrChildPath(currentDirectory, appBaseDirectory))
        {
            return;
        }

        string? sourceProjectDirectory = FindSourceProjectDirectory(appBaseDirectory);
        if (sourceProjectDirectory is not null)
        {
            Environment.CurrentDirectory = FindGitRepositoryRoot(sourceProjectDirectory) ?? sourceProjectDirectory;
        }
    }

    private static string? FindSourceProjectDirectory(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Potato.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindGitRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsSameOrChildPath(string candidatePath, string parentPath)
    {
        string candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string parent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
