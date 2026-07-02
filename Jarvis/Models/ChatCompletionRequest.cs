using System.Text.Json.Serialization;

namespace Jarvis.Models
{
    public class ChatCompletionRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "phi-silica";

        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = new();

        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = false;

        // NIEUW: De lijst met tools (functies) die Rider beschikbaar stelt aan het model
        [JsonPropertyName("tools")]
        public List<ChatCompletionTool>? Tools { get; set; }

        // NIEUW: Optioneel, bijv. "auto", "none", of een specifiek object
        [JsonPropertyName("tool_choice")]
        public object? ToolChoice { get; set; }

        // NIEUW: Temperature setting for controlling randomness
        [JsonPropertyName("temperature")]
        public float? Temperature { get; set; }
        
        [JsonPropertyName("max_tokens")]
        public int? MaxTokenLength { get; set; }

        [JsonPropertyName("max_completion_tokens")]
        public int? MaxCompletionTokens { get; set; }
    }
}
