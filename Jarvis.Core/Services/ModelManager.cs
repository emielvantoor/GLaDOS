using Jarvis.Core.Models;

namespace Jarvis.Core.Services;

public class ModelManager(IEnumerable<LanguageModel> languageModels) : IModelManager
{
    private Dictionary<string, LanguageModel> languageModelCache = languageModels.ToDictionary(x => x.ModelMetaData.Id);
    
    public ICollection<LanguageModelMetaData> GetAvailableModels()
    {
        return languageModels.Select(x => x.ModelMetaData).ToList();
    }

    public async Task<LanguageModel> GetAndInitializeModel(string modelName)
    {
        var model = languageModelCache[modelName];
        if (model == null)
        {
            throw new KeyNotFoundException(modelName);
        }

        await model.InitializeAsync();

        return model;
    }
}