namespace Rewrite;

internal sealed class CommandTranslator(GladosClient gladosClient)
{
    public async Task<CommandTranslationResult> TranslateAsync(string wise)
    {
        if (CommandTemplates.TryTranslate(wise, out string? knownCommand))
        {
            return CommandTranslationResult.Translated(knownCommand!);
        }

        bool understood = await gladosClient.CanUnderstandAsync(wise);
        if (!understood)
        {
            return CommandTranslationResult.Failed("The request is ambiguous. Add the target text, file pattern, or operation.");
        }

        string command = await gladosClient.GenerateShellCommandAsync(wise);
        if (string.IsNullOrWhiteSpace(command))
        {
            return CommandTranslationResult.Failed("GLaDOS returned an empty command.");
        }

        command = ShellCommandSanitizer.Normalize(command);
        return ShellCommandSanitizer.IsSafeSingleCommand(command)
            ? CommandTranslationResult.Translated(command)
            : CommandTranslationResult.Failed("Generated command was not a single safe shell command.");
    }
}
