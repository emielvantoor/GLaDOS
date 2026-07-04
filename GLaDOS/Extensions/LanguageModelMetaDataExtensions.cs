using GLaDOS.Core.Interfaces;
using GLaDOS.Core.Models;
using GLaDOS.Models;

namespace GLaDOS.Extensions;

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
        var role = ToDomainRole(message.Role);

        if (role == AgentRole.Assistant && message.ToolCalls is { Count: > 0 })
        {
            var toolCall = message.ToolCalls[0];
            return new AgentMessage(
                role,
                message.Content ?? string.Empty,
                toolCall.Function.Name,
                toolCall.Function.Arguments);
        }

        if (role == AgentRole.Tool)
        {
            return new AgentMessage(
                role,
                message.Content ?? string.Empty,
                message.Name);
        }

        return new AgentMessage(ToDomainRole(message.Role), message.Content ?? string.Empty);
    }

    public static AgentToolDefinition ToDomainModel(this ChatCompletionTool tool)
    {
        return new AgentToolDefinition(
            tool.Function.Name,
            tool.Function.Description ?? string.Empty,
            tool.Function.Parameters,
            tool.Permitted);
    }
}
