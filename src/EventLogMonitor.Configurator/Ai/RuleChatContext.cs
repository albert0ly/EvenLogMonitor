using System.Text.Json;

namespace EventLogMonitor.Configurator.Ai;

public sealed record RuleChatContext(IReadOnlyList<EventRule> Rules, int SelectedIndex)
{
    public string Describe()
    {
        if (Rules.Count == 0) return "Notification Rules list is empty. No rule is selected.";
        var lines = Rules.Select((rule, index) =>
            $"Rule #{index + 1}" + (index == SelectedIndex ? " (selected)" : "") + ": " + JsonSerializer.Serialize(rule));
        return $"Notification Rules list has {Rules.Count} rule(s), in display order. The last rule is #{Rules.Count}.\n" +
               string.Join("\n", lines) +
               (SelectedIndex < 0 ? "\nNo rule is selected." : "");
    }

    public static EventRule Copy(EventRule rule) => new()
    {
        Name = rule.Name, Enabled = rule.Enabled, LogName = rule.LogName,
        EventIds = [.. rule.EventIds], Providers = [.. rule.Providers], Levels = [.. rule.Levels],
        MessageContains = rule.MessageContains, MessageRegex = rule.MessageRegex,
        ApplicationPools = [.. rule.ApplicationPools], CooldownSeconds = rule.CooldownSeconds,
        NotifyEmail = rule.NotifyEmail, NotifySms = rule.NotifySms
    };
}
