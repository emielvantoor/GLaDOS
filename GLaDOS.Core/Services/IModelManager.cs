using GLaDOS.Core.Models;

namespace GLaDOS.Core.Services;

public interface IModelManager
{
    ICollection<LanguageModelMetaData> GetAvailableModels();
    Task<LanguageModel> GetAndInitializeModel(string modelName);
}