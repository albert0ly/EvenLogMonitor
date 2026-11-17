using System.Text.Json.Serialization;

namespace EventLogMonitor.Configurator.Ai;

/// <summary>What the model returns. Intentionally small: intent, not raw configuration.</summary>
public sealed class DraftResult
{
    [JsonPropertyName("clarification")] public string Clarification { get; set; } = "";
    [JsonPropertyName("rules")] public List<RuleDraft> Rules { get; set; } = [];
}

public sealed class RuleDraft
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("scenario")] public string Scenario { get; set; } = "Custom";
    [JsonPropertyName("levels")] public List<string> Levels { get; set; } = [];
    [JsonPropertyName("applicationPools")] public List<string> ApplicationPools { get; set; } = [];
    [JsonPropertyName("messageContains")] public string MessageContains { get; set; } = "";
    [JsonPropertyName("notifyEmail")] public bool NotifyEmail { get; set; } = true;
    [JsonPropertyName("notifySms")] public bool NotifySms { get; set; }
    [JsonPropertyName("cooldownSeconds")] public int CooldownSeconds { get; set; } = 60;

    // Used only when scenario is Custom.
    [JsonPropertyName("logName")] public string LogName { get; set; } = "";
    [JsonPropertyName("providers")] public List<string> Providers { get; set; } = [];
    [JsonPropertyName("eventIds")] public List<int> EventIds { get; set; } = [];
}
