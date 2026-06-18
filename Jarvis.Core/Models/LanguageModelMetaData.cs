namespace Jarvis.Core.Models;

public class LanguageModelMetaData
{
    public string Id { get; set; } = "gpt-4o";

    public string Object { get; set; } = "model";

    public long Created { get; set; } = 1717830000;

    public string OwnedBy { get; set; } = "openai";

    public List<LanguageModelPermission> Permission { get; set; } = [ new() ];
}