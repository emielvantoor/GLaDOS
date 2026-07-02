using Jarvis.Core.Models;
using LLama.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.LLama.Bootstrapper;

public static class AddLLamaModelBootstrapper
{
    public static void AddLLamaModels(this IServiceCollection services)
    {
        var nextCoderMetaData = new LanguageModelMetaData
        {
            Id = "local-gguf",
    
            Object = "model",
    
            // Unix timestamp van de release (bijvoorbeeld juni 2026 of de originele releasedatum)
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 
    
            OwnedBy = "local",
            
            ContextLength =  8192,
            
            MaxOutputTokens =  -1,
    
            Permission = [
                new LanguageModelPermission
                {
                    Id = $"modelperm-{Guid.NewGuid()}",
                    Object = "model_permission",
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    AllowCreateEngine = true,
                    AllowSampling = true,
                    AllowLogprobs = false, // LLamaSharp ondersteunt logprobs, maar voor basis code-completion vaak niet nodig
                    AllowSearchIndices = false,
                    AllowView = true,
                    AllowFineTuning = false,
                    Organization = "*",
                    Group = null,
                    IsBlocking = false
                }
            ]
        };
        
        services.AddSingleton<LanguageModel, LLamaLanguageModel>(provider =>
        {
            var modelParams = provider.GetRequiredService<ModelParams>();
            nextCoderMetaData.Id = Path.GetFileNameWithoutExtension(modelParams.ModelPath);
            nextCoderMetaData.ContextLength = modelParams.ContextSize.HasValue ? (int) modelParams.ContextSize.Value : 0;
            return new LLamaLanguageModel(nextCoderMetaData, modelParams);
        });
    }

}
