using Jarvis.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Onnx.Bootstrapper;

public static class AddOnnxModelsBootstrapper
{
    public static void AddOnnxModels(this IServiceCollection services)
    {
        services.AddSingleton<LanguageModel, OnnxLanguageModel>();
    }
}