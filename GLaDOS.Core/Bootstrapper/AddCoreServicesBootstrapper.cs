using GLaDOS.Core.Agents;
using GLaDOS.Core.Protocols;
using GLaDOS.Core.Routing;
using GLaDOS.Core.Services;
using GLaDOS.Core.ToolAdapters;
using GLaDOS.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace GLaDOS.Core.Bootstrapper;

public static class AddCoreServicesBootstrapper
{
    public static void AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IModelManager, ModelManager>();
        services.AddSingleton<IAgentProtocol, QwenProtocol>();
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<ToolRouter>();
        services.AddSingleton<ToolCallAdapterPipeline>();
        services.AddSingleton<IToolCallAdapter, SingleValueArgumentAdapter>();
        services.AddSingleton<IToolCallAdapter, QwenEditToolCallAdapter>();
        services.AddSingleton<GLaDOSAgent>();
        services.AddSingleton<IAgentTool, SystemTimeTool>();
        services.AddSingleton<IAgentTool, TemperatureTool>();
    }
}
