using System.Text.Json.Serialization;

namespace GLaDOS.Models;

public class ModelListResponse
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "list";

    [JsonPropertyName("data")]
    public List<ModelData> Data { get; set; } = new();
}