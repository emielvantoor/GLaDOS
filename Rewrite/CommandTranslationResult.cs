namespace Rewrite;

internal sealed record CommandTranslationResult(bool Success, string? Command, string? Error)
{
    public static CommandTranslationResult Translated(string command) => new(true, command, null);

    public static CommandTranslationResult Failed(string error) => new(false, null, error);
}
