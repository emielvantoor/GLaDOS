using System.Text.Json.Nodes;

namespace GLaDOS.Core.Tools;

public interface IAgentTool
{
    // De exacte unieke naam van de tool voor de GPU (bijv. "get_system_time")
    string Name { get; }
    
    // De beschrijving zodat de GPU weet WANNEER hij deze tool moet kiezen
    string Description { get; }

    ToolPermission Permitted { get; }
    
    // De OpenAI-compatible JSON schema definitie van de parameters (indien aanwezig)
    JsonObject Parameters { get; }

    // De daadwerkelijke C# code die wordt uitgevoerd als de GPU deze tool kiest
    Task<string> ExecuteAsync(JsonObject arguments);
}
