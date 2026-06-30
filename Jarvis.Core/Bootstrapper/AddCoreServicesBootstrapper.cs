using Jarvis.Core.Agents;
using Jarvis.Core.Services;
using Jarvis.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Core.Bootstrapper;

public static class AddCoreServicesBootstrapper
{
    public static void AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IModelManager, ModelManager>();
        services.AddSingleton<JarvisAgent>();
        services.AddSingleton<IJarvisTool, SystemTimeTool>();
        services.AddSingleton<IJarvisTool, TemperatureTool>();
    }
}