using Jarvis.Core.Models;
using Jarvis.Core.Interfaces;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace Jarvis.Onnx;

public class OnnxLanguageModel : LanguageModel
{
    // 1. Pad naar de map waar je ONNX model en tokenizer bestanden staan
    private const string ModelPath = @"C:\Users\Emiel\Downloads\amd-Qwen2.5-Coder-7B-Instruct-onnx-ryzenai-hybrid";

    private Model? model;
    private Tokenizer? tokenizer;
    private bool isInitialized;

    public override LanguageModelMetaData ModelMetaData { get; } = new();

    protected override Task OnInitializeAsync()
    {
        if (isInitialized)
        {
            return Task.CompletedTask;
        }
        
        // 2. Initialiseer de ONNX Runtime GenAI omgeving
        model = new Model(ModelPath);
        tokenizer = new Tokenizer(model);
        isInitialized = true;

        return Task.CompletedTask;
    }

    protected override async Task<string> OnGenerateResponseAsync(
        string prompt,
        ChatOptions options,
        CancellationToken cancellationToken = default)
    {
        // 2. Bereid de prompt voor
        using var tokens = tokenizer!.Encode(prompt);

        // 3. Vul de parameters (In C# zet je de tokens direct in de constructor van Generator)
        using var generatorParams = new GeneratorParams(model!);
        generatorParams.SetSearchOption("max_length", 2048);

        // 4. Maak de Generator aan
        using var generator = new Generator(model!, generatorParams);

        // 5. Gebruik een TokenizerStream voor veilig incrementeel streamen (voorkomt rare tekens)
        using var tokenizerStream = tokenizer.CreateStream();
        var responseBuilder = new System.Text.StringBuilder();

        // Voeg de start-tokens toe aan de generator loop
        generator.AppendTokenSequences(tokens);

        // 6. De daadwerkelijke, werkende lus
        while (!generator.IsDone())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return responseBuilder.ToString().Trim();
            }
            
            // Dit berekent de logits én kiest het volgende token in één klap!
            generator.GenerateNextToken();
    
            // Haal het allernieuwste token op via de handige .NET indexer [^1]
            var lastToken = generator.GetSequence(0)[^1];
    
            // Decodeer het token veilig naar tekst
            string textChunk = tokenizerStream.Decode(lastToken);
            
            responseBuilder.Append(textChunk);
            await Task.Yield();
        }

        return responseBuilder.ToString().Trim();
    }
}
