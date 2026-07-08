namespace Rewrite;

internal static class Program
{
    private static async Task Main()
    {
        var client = GladosClient.FromEnvironment();
        var translator = new CommandTranslator(client);

        Console.WriteLine("Rewrite - translate wise input into shell commands.");
        Console.WriteLine("Type a request, or 'exit' to quit.");

        while (true)
        {
            Console.Write("> ");
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CommandTranslationResult result = await translator.TranslateAsync(input);
            if (!result.Success)
            {
                Console.WriteLine($"Could not translate request: {result.Error}");
                continue;
            }

            Console.WriteLine(result.Command);
        }
    }
}
