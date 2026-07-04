using Jarvis.Core.Models;

namespace Jarvis.Core.Services;

public class ModelManager(IEnumerable<LanguageModel> languageModels) : IModelManager
{
    private readonly Dictionary<string, LanguageModel> languageModelCache = languageModels.ToDictionary(x => x.ModelMetaData.Id);
    private readonly SemaphoreSlim modelSwitchLock = new(1, 1);
    private string? currentModelName;
    
    public ICollection<LanguageModelMetaData> GetAvailableModels()
    {
        return languageModels.Select(x => x.ModelMetaData).ToList();
    }

    public async Task<LanguageModel> GetAndInitializeModel(string modelName)
    {
        await modelSwitchLock.WaitAsync();
        try
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

            if (currentModelName is not null &&
                !string.Equals(currentModelName, model.ModelMetaData.Id, StringComparison.Ordinal))
            {
                await languageModelCache[currentModelName].UnloadAsync();
                currentModelName = null;
            }

            await model.InitializeAsync();
            currentModelName = model.ModelMetaData.Id;

            return model;
        }
        finally
        {
            modelSwitchLock.Release();
        }
    }
}
