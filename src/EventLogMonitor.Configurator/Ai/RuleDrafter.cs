using System.Text.Json;
using System.Text.RegularExpressions;

namespace EventLogMonitor.Configurator.Ai;

public sealed record DraftOutcome(IReadOnlyList<EventRule> Rules, string? Clarification, IReadOnlyList<string> Warnings);

/// <summary>Asks the local model for a draft, then validates and expands it into real EventRule objects.
/// Nothing the model says reaches a rule unchecked: scenarios expand from the catalog, pools must exist in IIS,
/// levels are normalised, and custom rules must name a real log and be specific.</summary>
public sealed class RuleDrafter
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly AiSettings _settings;

    public RuleDrafter(AiSettings settings) => _settings = settings;

    public async Task<DraftOutcome> DraftAsync(string request, IReadOnlyCollection<string> existingRuleNames,
        IReadOnlyCollection<string> knownLogNames, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request))
            return new DraftOutcome([], "Describe what you want to watch first.", []);

        var iis = IisApplicationPoolCatalog.Read();
        var messages = PromptBuilder.BuildMessages(request, iis.ApplicationPools, iis.Error is null);
        return await DraftConversationAsync(messages, existingRuleNames, knownLogNames, iis, ct);
    }

    public async Task<DraftOutcome> DraftConversationAsync(IReadOnlyList<ChatTurn> conversation,
        IReadOnlyCollection<string> existingRuleNames, IReadOnlyCollection<string> knownLogNames,
        CancellationToken ct = default)
    {
        var iis = IisApplicationPoolCatalog.Read();
        var system = PromptBuilder.BuildMessages("", iis.ApplicationPools, iis.Error is null)[0];
        var messages = new List<ChatTurn> { system };
        messages.AddRange(conversation.Where(turn => turn.Role is "user" or "assistant"));
        return await DraftConversationAsync(messages, existingRuleNames, knownLogNames, iis, ct);
    }

    private async Task<DraftOutcome> DraftConversationAsync(IReadOnlyList<ChatTurn> messages,
        IReadOnlyCollection<string> existingRuleNames, IReadOnlyCollection<string> knownLogNames,
        IisApplicationPoolCatalog.Snapshot iis, CancellationToken ct)
    {
        var userText = string.Join(" ", messages.Where(m => m.Role == "user").Select(m => m.Content));
        if (LooksLikeIisExpress(userText))
        {
            var levels = userText.Contains("critical", StringComparison.OrdinalIgnoreCase)
                ? new List<string> { "Critical", "Error" }
                : new List<string>();
            var expressResult = new DraftResult
            {
                Rules = [new RuleDraft
                {
                    Name = "ASP.NET Core IIS Express monitoring",
                    Scenario = nameof(Scenario.AspNetCoreIisExpress),
                    Levels = levels,
                    ApplicationPools = [],
                    NotifyEmail = true,
                    CooldownSeconds = 60
                }]
            };
            var outcome = ToRules(expressResult, iis, iis.Error is null, existingRuleNames, knownLogNames);
            return outcome with
            {
                Warnings = outcome.Warnings.Concat(["IIS Express is not full IIS: this monitors .NET/application crashes and errors. It cannot start, stop, reset, or restart the IIS Express process."]).ToList()
            };
        }

        using var client = new OllamaClient(_settings);
        var problem = await client.CheckAsync(ct);
        if (problem is not null) throw new InvalidOperationException(problem);
        var raw = await client.ChatJsonAsync(messages, PromptBuilder.BuildSchema(), ct);

        DraftResult? result;
        try { result = JsonSerializer.Deserialize<DraftResult>(raw, Json); }
        catch (JsonException ex) { throw new InvalidOperationException("The model returned invalid JSON: " + ex.Message); }
        if (result is null) throw new InvalidOperationException("The model returned no result.");
        return ToRules(result, iis, iis.Error is null, existingRuleNames, knownLogNames);
    }

    /// <summary>Pure conversion/validation step (no network), kept separate so it can be tested.</summary>
    public static DraftOutcome ToRules(DraftResult result, IisApplicationPoolCatalog.Snapshot iis, bool poolsKnown,
        IReadOnlyCollection<string> existingRuleNames, IReadOnlyCollection<string> knownLogNames)
    {
        var warnings = new List<string>();
        if (result.Rules.Count == 0)
            return new DraftOutcome([], string.IsNullOrWhiteSpace(result.Clarification)
                ? "I could not turn that into a rule. Please rephrase and name the application or event you care about."
                : result.Clarification.Trim(), warnings);

        var used = new HashSet<string>(existingRuleNames, StringComparer.OrdinalIgnoreCase);
        var rules = new List<EventRule>();

        foreach (var draft in result.Rules)
        {
            // Pools: must exist when IIS can be enumerated.
            var pools = new List<string>();
            var missing = new List<string>();
            foreach (var requested in draft.ApplicationPools.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                if (!poolsKnown) { pools.Add(requested.Trim()); continue; }
                var match = ResolvePool(requested.Trim(), iis.ApplicationPools);
                if (match is null) missing.Add(requested.Trim()); else pools.Add(match);
            }
            if (missing.Count > 0)
                return new DraftOutcome([], $"IIS application pool(s) not found on this server: {string.Join(", ", missing)}. " +
                    $"Available: {(iis.ApplicationPools.Count == 0 ? "none" : string.Join(", ", iis.ApplicationPools))}. Which one did you mean?", warnings);
            if (!poolsKnown && pools.Count > 0)
                warnings.Add("IIS could not be read, so pool names were not verified (" + (iis.Error ?? "unknown reason") + ").");

            var levels = NormalizeLevels(draft.Levels);
            var cooldown = Math.Clamp(draft.CooldownSeconds < 0 ? 60 : draft.CooldownSeconds, 0, 86400);
            var email = draft.NotifyEmail;
            var sms = draft.NotifySms;
            if (!email && !sms) email = true;

            var scenario = Enum.TryParse<Scenario>(draft.Scenario, true, out var s) ? s : Scenario.Custom;
            var baseName = string.IsNullOrWhiteSpace(draft.Name) ? scenario.ToString() : draft.Name.Trim();

            if (scenario == Scenario.Custom)
            {
                if (string.IsNullOrWhiteSpace(draft.LogName))
                    return new DraftOutcome([], "Which event log should I watch (Application, System, Security, or another channel)?", warnings);
                var log = knownLogNames.FirstOrDefault(l => string.Equals(l, draft.LogName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (log is null)
                    return new DraftOutcome([], $"The event log '{draft.LogName}' does not exist on this machine. Which log did you mean?", warnings);
                var ids = draft.EventIds.Where(i => i > 0).Distinct().ToList();
                var providers = draft.Providers.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
                if (ids.Count == 0 && providers.Count == 0 && levels.Count == 0 && string.IsNullOrWhiteSpace(draft.MessageContains))
                    return new DraftOutcome([], "That would match every event in the log. Please name an event ID, a source, a severity or a phrase.", warnings);

                rules.Add(Build(UniqueName(baseName, used), log, providers, ids, levels, draft.MessageContains, pools, cooldown, email, sms));
                warnings.Add($"'{baseName}' is a custom rule: its log, source and event IDs came from the model. Verify with Load recent events / Preview matches.");
                continue;
            }

            var entries = ScenarioCatalog.Items[scenario].Entries;
            foreach (var entry in entries)
            {
                var name = entries.Length > 1 ? $"{baseName} [{entry.LogName}]" : baseName;
                var message = entry.MessageContains;
                if (!string.IsNullOrWhiteSpace(draft.MessageContains))
                {
                    if (message.Length == 0) message = draft.MessageContains.Trim();
                    else warnings.Add($"'{baseName}': the phrase '{draft.MessageContains}' was ignored because this scenario already filters on '{message}'.");
                }
                rules.Add(Build(UniqueName(name, used), entry.LogName, entry.Providers.ToList(), entry.EventIds.ToList(),
                    levels.Count > 0 ? levels : entry.DefaultLevels.ToList(), message, pools, cooldown, email, sms));
            }
        }

        return new DraftOutcome(rules, null, warnings);
    }

    private static bool LooksLikeIisExpress(string text) =>
        text.Contains("iis express", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("localhost:", StringComparison.OrdinalIgnoreCase) &&
        (text.Contains("visual studio", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("asp.net core", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(".net 5", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(".net 6", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(".net 7", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(".net 8", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(".net 9", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(".net 10", StringComparison.OrdinalIgnoreCase));

    private static EventRule Build(string name, string log, List<string> providers, List<int> ids, List<string> levels,
        string message, List<string> pools, int cooldown, bool email, bool sms) => new()
    {
        Name = name, Enabled = true, LogName = log, Providers = providers, EventIds = ids, Levels = levels,
        MessageContains = message?.Trim() ?? "", ApplicationPools = pools, CooldownSeconds = cooldown,
        NotifyEmail = email, NotifySms = sms
    };

    private static string UniqueName(string name, HashSet<string> used)
    {
        var candidate = name; var n = 2;
        while (!used.Add(candidate)) candidate = $"{name} ({n++})";
        return candidate;
    }

    private static string? ResolvePool(string requested, IReadOnlyList<string> available)
    {
        var exact = available.FirstOrDefault(p => string.Equals(p, requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        // Accept a single unambiguous partial match, e.g. "Claims" -> "ClaimsAppPool".
        var partial = available.Where(p => p.Contains(requested, StringComparison.OrdinalIgnoreCase) ||
                                           requested.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    private static List<string> NormalizeLevels(IEnumerable<string> levels)
    {
        var result = new List<string>();
        foreach (var level in levels)
        {
            var normalized = level.Trim().ToLowerInvariant() switch
            {
                "critical" => "Critical", "error" => "Error", "warning" => "Warning",
                "information" or "informational" or "info" => "Information",
                "verbose" or "debug" => "Verbose", _ => null
            };
            if (normalized is not null && !result.Contains(normalized)) result.Add(normalized);
        }
        return result;
    }
}
