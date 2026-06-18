using Jarvis.Core.Models;
using Jarvis.Models;

namespace Jarvis.Extensions;

public static class LanguageModelMetaDataExtensions
{
    public static ModelData ToDto(this LanguageModelMetaData metaData)
    {
        return new ModelData
        {
            Id = metaData.Id,
            OwnedBy = metaData.OwnedBy,
            Created = metaData.Created,
            Object = metaData.Object,
            Permission = [.. metaData.Permission.Select(p => p.ToDto())]
        };
    }

    public static ModelPermission ToDto(this LanguageModelPermission permission)
    {
        return new ModelPermission
        {
            Id = permission.Id,
            Created = permission.Created,
            Object = permission.Object,
            AllowCreateEngine = permission.AllowCreateEngine,
            AllowSampling = permission.AllowSampling,
            AllowLogprobs = permission.AllowLogprobs,
            AllowSearchIndices = permission.AllowSearchIndices,
            AllowView = permission.AllowView,
            AllowFineTuning = permission.AllowFineTuning,
            Group = permission.Group,
            IsBlocking = permission.IsBlocking,
            Organization = permission.Organization
        };
    }
}