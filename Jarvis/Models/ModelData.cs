using System.Text.Json.Serialization;

namespace Jarvis.Models;

// Update je ModelData klasse in de Models namespace:
public class ModelData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "gpt-4o";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "model";

    [JsonPropertyName("created")]
    public long Created { get; set; } = 1717830000;

    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; set; } = "openai";
    
    [JsonPropertyName("context_length")]
    public int ContextLength { get; set; }
    
    [JsonPropertyName("max_output_tokens")]
    public int MaxOutputTokens { get; set; }
    
    [JsonPropertyName("permission")]
    public List<ModelPermission> Permission { get; set; } = [ new() ];
}

// Request Modellen

// Response Modellen (Non-Streaming)

// Response Modellen (Streaming)