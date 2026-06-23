using Jarvis.Core.Interfaces;
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
            ContextLength = metaData.ContextLength,
            MaxOutputTokens = metaData.MaxOutputTokens,
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

    /// <summary>
    /// Converts a string representation of an agent role to the corresponding AgentRole enum value.
    /// </summary>
    /// <param name="role">The string representation of the agent role.</param>
    /// <returns>The corresponding AgentRole enum value.</returns>
    public static AgentRole ToDomainRole(string role)
    {
        return role.ToLower() switch
        {
            "system" => AgentRole.System,
            "assistant" => AgentRole.Assistant,
            "tool" => AgentRole.Tool,
            _ => AgentRole.User
        };
    }

    public static AgentMessage ToDomainModel(this ChatMessage message)
    {
        return new AgentMessage(ToDomainRole(message.Role), message.Content);
    }

    public static AgentToolDefinition ToDomainModel(this ChatCompletionTool tool)
    {
        return new AgentToolDefinition(tool.Function.Name, tool.Function.Description, tool.Function.Parameters);
    }
}