using Jarvis.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.LLama.Bootstrapper;

public static class AddLLamaModelBootstrapper
{
    public static void AddLLamaModels(this IServiceCollection services, IConfiguration configuration)
    {
        foreach (var modelPath in GetModelPaths(configuration))
        {
            services.AddSingleton<LanguageModel, LLamaLanguageModel>(_ =>
            {
                var modelParams = LLamaHardwareConfigurator.CreateOptimizedParameters(configuration, modelPath);
                return new LLamaLanguageModel(CreateMetaData(modelParams.ModelPath, modelParams.ContextSize), modelParams);
            });
        }
    }

    private static IReadOnlyCollection<string> GetModelPaths(IConfiguration configuration)
    {
        var configuredPath = configuration["Jarvis:ModelPath"] ??
                             throw new ArgumentNullException("ModelPath is niet ingesteld in appsettings.json");

        if (Directory.Exists(configuredPath))
        {
            var modelPaths = Directory
                .EnumerateFiles(configuredPath, "*.gguf", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (modelPaths.Length == 0)
            {
                throw new FileNotFoundException($"Geen .gguf modellen gevonden in '{configuredPath}'.");
            }

            return modelPaths;
        }

        if (!File.Exists(configuredPath))
        {
            throw new FileNotFoundException($"ModelPath verwijst niet naar een bestaand bestand of map: '{configuredPath}'.");
        }

        if (!string.Equals(Path.GetExtension(configuredPath), ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"ModelPath bestand moet een .gguf model zijn: '{configuredPath}'.");
        }

        return [configuredPath];
    }

    private static LanguageModelMetaData CreateMetaData(string modelPath, uint? contextSize)
    {
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new LanguageModelMetaData
        {
            Id = Path.GetFileNameWithoutExtension(modelPath),
            Object = "model",
            Created = created,
            OwnedBy = "local",
            ContextLength = contextSize.HasValue ? (int)contextSize.Value : 0,
            MaxOutputTokens = -1,
            Permission =
            [
                new LanguageModelPermission
                {
                    Id = $"modelperm-{Guid.NewGuid()}",
                    Object = "model_permission",
                    Created = created,
                    AllowCreateEngine = true,
                    AllowSampling = true,
                    AllowLogprobs = false,
                    AllowSearchIndices = false,
                    AllowView = true,
                    AllowFineTuning = false,
                    Organization = "*",
                    Group = null,
                    IsBlocking = false
                }
            ]
        };
    }
}
