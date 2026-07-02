using Jarvis.Core.Models;

namespace Jarvis.Core.Services;

public class ModelManager(IEnumerable<LanguageModel> languageModels) : IModelManager
{
    private readonly Dictionary<string, LanguageModel> languageModelCache = languageModels.ToDictionary(x => x.ModelMetaData.Id);
    
    public ICollection<LanguageModelMetaData> GetAvailableModels()
    {
        return languageModels.Select(x => x.ModelMetaData).ToList();
    }

    public async Task<LanguageModel> GetAndInitializeModel(string modelName)
    {
        if (!languageModelCache.TryGetValue(modelName, out var model))
        {
            if (languageModelCache.Count == 1)
            {
                model = languageModelCache.Values.Single();
            }
            else
            {
                throw new KeyNotFoundException(modelName);
            }
        }

        await model.InitializeAsync();

        return model;
    }
}
