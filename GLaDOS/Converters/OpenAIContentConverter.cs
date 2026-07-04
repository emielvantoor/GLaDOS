using System.Text.Json;
using System.Text.Json.Serialization;

namespace GLaDOS.Converters;

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
                else if (TryReadImageUrl(element, out var imageUrl))
                {
                    textParts.Add($"[Image: {imageUrl}]");
                }
                else if (TryReadFileReference(element, out var fileReference))
                {
                    textParts.Add($"[File: {fileReference}]");
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

    private static bool TryReadImageUrl(JsonElement element, out string imageUrl)
    {
        imageUrl = string.Empty;

        if (!element.TryGetProperty("image_url", out var imageUrlElement))
        {
            return false;
        }

        if (imageUrlElement.ValueKind == JsonValueKind.String)
        {
            imageUrl = imageUrlElement.GetString() ?? string.Empty;
        }
        else if (imageUrlElement.ValueKind == JsonValueKind.Object &&
                 imageUrlElement.TryGetProperty("url", out var urlElement))
        {
            imageUrl = urlElement.GetString() ?? string.Empty;
        }

        return !string.IsNullOrWhiteSpace(imageUrl);
    }

    private static bool TryReadFileReference(JsonElement element, out string fileReference)
    {
        fileReference = string.Empty;

        if (!element.TryGetProperty("file", out var fileElement) ||
            fileElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (fileElement.TryGetProperty("filename", out var filenameElement))
        {
            fileReference = filenameElement.GetString() ?? string.Empty;
        }
        else if (fileElement.TryGetProperty("file_id", out var fileIdElement))
        {
            fileReference = fileIdElement.GetString() ?? string.Empty;
        }

        return !string.IsNullOrWhiteSpace(fileReference);
    }
}
