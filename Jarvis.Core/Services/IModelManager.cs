using Jarvis.Core.Models;

namespace Jarvis.Core.Services;

public interface IModelManager
{
    ICollection<LanguageModelMetaData> GetAvailableModels();
    Task<LanguageModel> GetAndInitializeModel(string modelName);
}