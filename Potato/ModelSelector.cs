using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Potato;

internal sealed class ModelSelector
{
    public async Task<string> SelectStartupModelAsync(Uri gladosEndpoint, string? selectedModel)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"Loading models from {gladosEndpoint}models...");
        Console.ResetColor();

        List<string> models = await GetAvailableModelsAsync(gladosEndpoint);
        if (!string.IsNullOrWhiteSpace(selectedModel))
        {
            string? matchingModel = models.FirstOrDefault(model =>
                string.Equals(model, selectedModel, StringComparison.OrdinalIgnoreCase));
            if (matchingModel is not null)
            {
                PotatoConsole.WriteStatus($"Using selected model from appsettings: {matchingModel}");
                return matchingModel;
            }

            if (models.Count > 0)
            {
                PotatoConsole.WriteStatus($"Saved model not found: {selectedModel}");
            }
        }

        return PromptForModel(models);
    }

    public async Task<string> PromptForModelAsync(Uri gladosEndpoint)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"Loading models from {gladosEndpoint}models...");
        Console.ResetColor();

        List<string> models = await GetAvailableModelsAsync(gladosEndpoint);
        return PromptForModel(models);
    }

    private static string PromptForModel(List<string> models)
    {
        if (models.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Available models:");
            Console.ResetColor();

            for (int i = 0; i < models.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {models[i]}");
            }

            while (true)
            {
                Console.Write("Choose a model by number or name: ");
                string? input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input))
                {
                    continue;
                }

                if (int.TryParse(input.Trim(), out int index) && index >= 1 && index <= models.Count)
                {
                    return models[index - 1];
                }

                string model = input.Trim();
                if (models.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    return models.First(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));
                }

                Console.WriteLine("Unknown model. Enter one of the listed numbers or model names.");
            }
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Could not load models from GLaDOS. Make sure GLaDOS is running, or enter a model id manually.");
        Console.ResetColor();

        while (true)
        {
            Console.Write("Model id: ");
            string? input = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(input))
            {
                return input.Trim();
            }
        }
    }

    public static async Task<List<string>> GetAvailableModelsAsync(Uri gladosEndpoint)
    {
        try
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = gladosEndpoint,
                Timeout = TimeSpan.FromSeconds(5)
            };

            var response = await httpClient.GetFromJsonAsync<ModelListResponse>("models");
            return response?.Data?
                .Select(model => model.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed class ModelListResponse
    {
        [JsonPropertyName("data")]
        public List<ModelData>? Data { get; set; }
    }

    private sealed class ModelData
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }
}