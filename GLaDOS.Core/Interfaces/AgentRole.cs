namespace GLaDOS.Core.Interfaces;

public enum AgentRole 
{ 
    System,      // <-- Voor de basisinstructies en tool-definities
    User,        // Voor de vragen van de gebruiker
    Assistant,   // Voor de antwoorden (of tool-intents) van de GPU
    Tool         // Voor de resultaten van je C# code
}