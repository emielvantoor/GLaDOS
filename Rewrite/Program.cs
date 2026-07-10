namespace Rewrite;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var client = GladosClient.FromEnvironment();
        var translator = new CommandTranslator(client);

        Console.WriteLine("Rewrite - translate wise input into shell commands.");
        Console.WriteLine("Usage: Rewrite.exe <wise>");

        var wise = string.Join(" ", args);
        Console.WriteLine("Wise: " + wise);

        if (string.IsNullOrWhiteSpace(wise))
        {
            Console.WriteLine("Empty wise, unable to translate");
            return;
        }
        
        CommandTranslationResult result = await translator.TranslateAsync(wise);
        if (!result.Success)
        {
            Console.WriteLine($"Could not translate request: {result.Error}");
            return;
        }

        Console.WriteLine(result.Command);
    }
}
