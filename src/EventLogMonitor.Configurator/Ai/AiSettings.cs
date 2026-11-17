using System.Text.Json;

namespace EventLogMonitor.Configurator.Ai;

/// <summary>Local-model settings for the rule drafter. Stored beside the Configurator executable
/// (ai-settings.json). The monitoring service never reads this file.</summary>
public sealed class AiSettings
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5:7b";
    public int TimeoutSeconds { get; set; } = 180;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "ai-settings.json");

    public static AiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(FilePath), Json) ?? new AiSettings();
        }
        catch { /* fall back to defaults */ }
        return new AiSettings();
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
}
