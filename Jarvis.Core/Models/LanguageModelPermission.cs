namespace Jarvis.Core.Models;

public class LanguageModelPermission
{
    public string Id { get; set; } = $"modelperm-{Guid.NewGuid()}";

    public string Object { get; set; } = "model_permission";

    public long Created { get; set; } = 1717830000;

    public bool AllowCreateEngine { get; set; } = true;

    public bool AllowSampling { get; set; } = true;

    public bool AllowLogprobs { get; set; } = true;

    public bool AllowSearchIndices { get; set; } = true;

    public bool AllowView { get; set; } = true;

    public bool AllowFineTuning { get; set; } = false;

    public string Organization { get; set; } = "*";

    public string? Group { get; set; } = null;

    public bool IsBlocking { get; set; } = false;
}