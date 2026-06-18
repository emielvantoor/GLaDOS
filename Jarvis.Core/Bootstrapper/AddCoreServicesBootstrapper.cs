using Jarvis.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Core.Bootstrapper;

public static class AddCoreServicesBootstrapper
{
    public static void AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IModelManager, ModelManager>();
    }
}