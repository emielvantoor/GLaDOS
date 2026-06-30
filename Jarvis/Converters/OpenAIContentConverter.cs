using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jarvis.Converters;

public class OpenAIContentConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Case 1: Content is a regular JSON String
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() ?? string.Empty;
        }

        // Case 2: Content is a structured JSON Array (e.g., Qwen/Multi-modal payloads)
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            using var jsonDoc = JsonDocument.ParseValue(ref reader);
            var root = jsonDoc.RootElement;
            var textParts = new List<string>();

            foreach (var element in root.EnumerateArray())
            {
                // Inspect OpenAI standard object parameters: [{"type": "text", "text": "..."}]
                if (element.TryGetProperty("text", out var textProp))
                {
                    textParts.Add(textProp.GetString() ?? string.Empty);
                }
                else if (element.ValueKind == JsonValueKind.String)
                {
                    textParts.Add(element.GetString() ?? string.Empty);
                }
            }

            return string.Join(Environment.NewLine, textParts);
        }

        // Fallback for other unexpected tokens (null, objects, numbers)
        using (var doc = JsonDocument.ParseValue(ref reader))
        {
            return doc.RootElement.GetRawText();
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}