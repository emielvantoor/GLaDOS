using System.Text.Json;
using System.Text.Json.Nodes;

namespace Potato;

public sealed class PotatoAppSettings
{
    public bool UseCompiledDefaultPrompts { get; init; }

    public bool WebUiInputEnabled { get; init; }

    public string? SelectedModel { get; init; }

    public string? ExecutionMode { get; init; }

    public int? ContextSize { get; init; }

    public bool? ContextOptimizationEnabled { get; init; }
}

public sealed class PotatoAppSettingsStore
{
    private const string UseCompiledDefaultPromptsProperty = "UseCompiledDefaultPrompts";
    private const string WebUiInputEnabledProperty = "WebUiInputEnabled";
    private const string SelectedModelProperty = "SelectedModel";
    private const string ExecutionModeProperty = "ExecutionMode";
    private const string ContextSizeProperty = "ContextSize";
    private const string ContextOptimizationEnabledProperty = "ContextOptimizationEnabled";
    private readonly string path;

    public PotatoAppSettingsStore(string path)
    {
        this.path = path;
    }

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public PotatoAppSettings Load()
    {
        JsonObject root = LoadRoot();
        return new PotatoAppSettings
        {
            UseCompiledDefaultPrompts = GetBool(root, UseCompiledDefaultPromptsProperty),
            WebUiInputEnabled = GetBool(root, WebUiInputEnabledProperty),
            SelectedModel = GetString(root, SelectedModelProperty),
            ExecutionMode = NormalizeExecutionMode(GetString(root, ExecutionModeProperty)),
            ContextSize = GetInt(root, ContextSizeProperty),
            ContextOptimizationEnabled = GetBool(root, ContextOptimizationEnabledProperty, defaultValue: true)
        };
    }

    public void SetUseCompiledDefaultPrompts(bool value)
    {
        JsonObject root = LoadRoot();
        root[UseCompiledDefaultPromptsProperty] = value;
        SaveRoot(root);
    }

    public void SetWebUiInputEnabled(bool value)
    {
        JsonObject root = LoadRoot();
        root[WebUiInputEnabledProperty] = value;
        SaveRoot(root);
    }

    public void SetSelectedModel(string model)
    {
        JsonObject root = LoadRoot();
        root[SelectedModelProperty] = model;
        SaveRoot(root);
    }

    public void SetExecutionMode(string mode)
    {
        JsonObject root = LoadRoot();
        root[ExecutionModeProperty] = NormalizeExecutionMode(mode) ?? "react";
        SaveRoot(root);
    }

    public void SetContextOptimizationEnabled(bool value)
    {
        JsonObject root = LoadRoot();
        root[ContextOptimizationEnabledProperty] = value;
        SaveRoot(root);
    }

    private JsonObject LoadRoot()
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            JsonNode? node = JsonNode.Parse(File.ReadAllText(path));
            return node as JsonObject ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveRoot(JsonObject root)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static bool GetBool(JsonObject root, string propertyName, bool defaultValue = false)
    {
        JsonNode? node = root[propertyName];
        if (node is null)
        {
            return defaultValue;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch
        {
            return bool.TryParse(node.ToString(), out bool value) && value ? true : defaultValue;
        }
    }

    private static string? GetString(JsonObject root, string propertyName)
    {
        JsonNode? node = root[propertyName];
        if (node is null)
        {
            return null;
        }

        try
        {
            string? value = node.GetValue<string>();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            string value = node.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    private static int? GetInt(JsonObject root, string propertyName)
    {
        JsonNode? node = root[propertyName];
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            return int.TryParse(node.ToString(), out int value) ? value : null;
        }
    }

    private static string? NormalizeExecutionMode(string? mode)
    {
        string normalized = mode?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "react" or "re-act" or "loop" => "react",
            "pipeline" or "plan" or "deterministic" => "pipeline",
            _ => "react"
        };
    }
}
