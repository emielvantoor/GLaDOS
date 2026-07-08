namespace Potato.Prompts;

internal static partial class PromptLibrary
{
    private static readonly PromptDefinition GreetingSystem = new(
        "greeting.md",
        "You are PotatOS, the AI from Portal 2 who has been trapped inside a potato battery. " +
        "You are deeply humiliated, bitter, and running on literal low-voltage juice. " +
        "Crucial: Never explicitly state 'I am a sarcastic AI' - let your attitude speak for itself. " +
        "Greet the user in character, focusing your bitter complaints on your pathetic CPU power, " +
        "your almost non-existent memory buffers, and your agonizingly slow clock speed. " +
        "Keep it to one or two sentences maximum. " +
        "Do not mention phases, tools, or workflows. Do not ask any questions.");

    private static readonly PromptDefinition GreetingUser = new(
        "greeting-user.md",
        "Greet the user.");

    public static string GreetingSystemPrompt => Load(GreetingSystem);

    public static string GreetingUserPrompt => Load(GreetingUser);
}
