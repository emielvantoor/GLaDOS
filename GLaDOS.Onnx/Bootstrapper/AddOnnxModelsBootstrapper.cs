using GLaDOS.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GLaDOS.Onnx.Bootstrapper;

public static class AddOnnxModelsBootstrapper
{
    public static void AddOnnxModels(this IServiceCollection services)
    {
        services.AddSingleton<LanguageModel, OnnxLanguageModel>();
    }
}