using System.Text.Json;
using System.Text.Json.Nodes;

namespace EventLogMonitor.Configurator.Ai;

public sealed record RuleChatReply(string Text, RuleEditProposal? Edit, IReadOnlyList<EventRule> NewRules);
public sealed record RuleEditProposal(int RuleIndex, string Description, EventRule Rule);

public sealed class RuleChatService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private static readonly HashSet<string> EditableFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "enabled", "logName", "eventIds", "providers", "levels", "messageContains", "messageRegex",
        "applicationPools", "cooldownSeconds", "notifyEmail", "notifySms"
    };
    private readonly AiSettings _settings;

    public RuleChatService(AiSettings settings) => _settings = settings;

    public async Task<RuleChatReply> ReplyAsync(IReadOnlyList<ChatTurn> conversation, RuleChatContext context,
        IReadOnlyCollection<string> knownLogs, CancellationToken ct = default)
    {
        if (conversation.Count == 0 || conversation[^1].Role != "user")
            throw new ArgumentException("The conversation must end with a user message.", nameof(conversation));

        var latest = conversation[^1].Content.Trim();
        if (IsLastRuleQuestion(latest))
        {
            var answer = context.Rules.Count == 0 ? "The Notification Rules list is empty."
                : $"Yes. The last Notification Rule is {FormatRule(context.Rules[^1], context.Rules.Count)}";
            return new RuleChatReply(answer, null, []);
        }
        if (TryGetRuleQuestion(latest, context, out var ruleAnswer))
            return new RuleChatReply(ruleAnswer, null, []);
        if (IsRuleListQuestion(latest))
            return new RuleChatReply(context.Rules.Count == 0 ? "The Notification Rules list is empty." :
                "Notification Rules:\n" + string.Join("\n", context.Rules.Select((rule, index) =>
                    $"#{index + 1} {rule.Name}" + (index == context.SelectedIndex ? " (selected)" : ""))), null, []);
        if (IsIisExpressDraftRequest(latest) && !System.Text.RegularExpressions.Regex.IsMatch(latest,
                @"\b(last|selected|current|rule\s*#?\d+|change|edit|update|modify)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var draft = await new RuleDrafter(_settings).DraftConversationAsync(conversation,
                context.Rules.Select(rule => rule.Name).ToList(), knownLogs, ct);
            if (draft.Clarification is not null) return new RuleChatReply(draft.Clarification, null, []);
            var description = "IIS Express monitoring proposal:\n" + FormatRules(draft.Rules, context.Rules.Count + 1);
            if (draft.Warnings.Count > 0) description += "\nNotes: " + string.Join("; ", draft.Warnings);
            return new RuleChatReply(description, null, draft.Rules);
        }

        var iis = IisApplicationPoolCatalog.Read();
        var messages = new List<ChatTurn> { PromptBuilder.BuildMessages("", iis.ApplicationPools, iis.Error is null, context)[0] };
        messages.AddRange(conversation.Where(turn => turn.Role is "user" or "assistant"));
        using var client = new OllamaClient(_settings);
        var problem = await client.CheckAsync(ct);
        if (problem is not null) throw new InvalidOperationException(problem);
        var raw = await client.ChatJsonAsync(messages, BuildSchema(), ct);
        ChatResponse response;
        try { response = JsonSerializer.Deserialize<ChatResponse>(raw, Json) ?? throw new JsonException("Empty response."); }
        catch (JsonException ex) { throw new InvalidOperationException("The model returned invalid JSON: " + ex.Message); }

        RuleEditProposal? edit = null;
        if (response.Edit is not null)
        {
            if (!RequestsEdit(latest) && !conversation.Take(conversation.Count - 1)
                    .Where(turn => turn.Role == "user").TakeLast(1).Any(turn => RequestsEdit(turn.Content)))
                throw new InvalidOperationException("The model proposed an edit without an explicit edit request. No changes were made.");
            if (response.Rules.Count > 0)
                throw new InvalidOperationException("The model tried to edit and create rules in one reply. No changes were made.");
            edit = ValidateEdit(response.Edit, context, knownLogs);
            var changes = (JsonObject)response.Edit.Changes!;
            var details = changes.ToJsonString(PrettyJson);
            return new RuleChatReply($"Proposed edit to rule #{edit.RuleIndex + 1} ({context.Rules[edit.RuleIndex].Name}):\n{details}\nClick Apply edit to update the rule in memory, then Save configuration.", edit, []);
        }

        IReadOnlyList<EventRule> newRules = [];
        var text = response.Answer?.Trim() ?? "";
        if (response.Draft.Rules.Count > 0)
        {
            var outcome = RuleDrafter.ToRules(response.Draft, iis, iis.Error is null,
                context.Rules.Select(r => r.Name).ToList(), knownLogs);
            if (outcome.Clarification is not null) text = outcome.Clarification;
            else
            {
                newRules = outcome.Rules;
                text = (text.Length == 0 ? $"Drafted {newRules.Count} new rule(s)." : text) +
                    (outcome.Warnings.Count == 0 ? "" : "\nNotes: " + string.Join("; ", outcome.Warnings));
            }
        }
        else if (text.Length == 0) text = response.Draft.Clarification?.Trim() ?? "";
        if (text.Length == 0) text = "I could not answer that. Please rephrase your question.";
        if (newRules.Count > 0)
            text += "\nProposed new rules:\n" + FormatRules(newRules, context.Rules.Count + 1);
        return new RuleChatReply(text, edit, newRules);
    }

    private static string FormatRule(EventRule rule, int number) =>
        $"Rule #{number} ({rule.Name}):\n{JsonSerializer.Serialize(rule, PrettyJson)}";

    private static string FormatRules(IReadOnlyList<EventRule> rules, int startNumber) =>
        string.Join("\n\n", rules.Select((rule, index) => FormatRule(rule, startNumber + index)));

    private static bool IsRuleListQuestion(string text) =>
        !RequestsEdit(text) &&
        System.Text.RegularExpressions.Regex.IsMatch(text,
            @"\b(list|show|enumerate|number|what|which)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
        System.Text.RegularExpressions.Regex.IsMatch(text, @"\brules\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsIisExpressDraftRequest(string text) =>
        (text.Contains("iis express", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("localhost:", StringComparison.OrdinalIgnoreCase) &&
         (text.Contains("visual studio", StringComparison.OrdinalIgnoreCase) ||
          text.Contains("asp.net", StringComparison.OrdinalIgnoreCase))) &&
        System.Text.RegularExpressions.Regex.IsMatch(text,
            @"\b(watch|monitor|alert|notify|draft|create)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsLastRuleQuestion(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"\blast\s+rule\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
        !RequestsEdit(text) &&
        System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(see|show|what|which|describe|tell me about)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool TryGetRuleQuestion(string text, RuleChatContext context, out string answer)
    {
        answer = "";
        if (RequestsEdit(text) || !System.Text.RegularExpressions.Regex.IsMatch(text,
                @"\b(see|show|what|which|describe|tell me about)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return false;
        var selected = System.Text.RegularExpressions.Regex.IsMatch(text,
            @"\b(selected|current)\s+rule\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var numbered = System.Text.RegularExpressions.Regex.Match(text,
            @"\brule\s*(?:#|number\s*)?(\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!selected && !numbered.Success) return false;
        var index = selected ? context.SelectedIndex :
            int.TryParse(numbered.Groups[1].Value, out var number) ? number - 1 : -1;
        answer = index >= 0 && index < context.Rules.Count
            ? FormatRule(context.Rules[index], index + 1) + (selected ? " (selected)" : "")
            : selected ? "No Notification Rule is selected." : $"Rule #{index + 1} is not in the Notification Rules list.";
        return true;
    }

    private static bool RequestsEdit(string text) =>
        new[] { "change", "edit", "update", "set", "enable", "disable", "rename", "remove", "add", "make", "switch" }
            .Any(verb => System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{verb}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    private static RuleEditProposal ValidateEdit(ChatEdit proposed, RuleChatContext context, IReadOnlyCollection<string> knownLogs)
    {
        var index = proposed.RuleIndex - 1;
        if (index < 0 || index >= context.Rules.Count)
            throw new InvalidOperationException("The proposed edit targets a rule that is not in the Notification Rules list.");
        if (proposed.Changes is not JsonObject changes || changes.Count == 0)
            throw new InvalidOperationException("The proposed edit contains no changes.");
        if (changes.Select(change => change.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Count)
            throw new InvalidOperationException("The proposed edit repeats a field with different capitalization.");
        var original = context.Rules[index];
        var updated = RuleChatContext.Copy(original);
        foreach (var (field, value) in changes)
        {
            if (!EditableFields.Contains(field) || value is null)
                throw new InvalidOperationException($"The proposed edit has an unsupported field: {field}.");
            try
            {
                switch (field.ToLowerInvariant())
                {
                    case "name": updated.Name = value.GetValue<string>(); break;
                    case "enabled": updated.Enabled = value.GetValue<bool>(); break;
                    case "logname": updated.LogName = value.GetValue<string>(); break;
                    case "eventids": updated.EventIds = value.Deserialize<List<int>>()!; break;
                    case "providers": updated.Providers = value.Deserialize<List<string>>()!; break;
                    case "levels": updated.Levels = value.Deserialize<List<string>>()!; break;
                    case "messagecontains": updated.MessageContains = value.GetValue<string>(); break;
                    case "messageregex": updated.MessageRegex = value.GetValue<string>(); break;
                    case "applicationpools": updated.ApplicationPools = value.Deserialize<List<string>>()!; break;
                    case "cooldownseconds": updated.CooldownSeconds = value.GetValue<int>(); break;
                    case "notifyemail": updated.NotifyEmail = value.GetValue<bool>(); break;
                    case "notifysms": updated.NotifySms = value.GetValue<bool>(); break;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new InvalidOperationException($"Invalid value for {field}.", ex);
            }
        }
        if (string.IsNullOrWhiteSpace(updated.Name) ||
            context.Rules.Where((_, i) => i != index).Any(r => string.Equals(r.Name, updated.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The proposed rule name is empty or already used.");
        if (!knownLogs.Contains(updated.LogName, StringComparer.OrdinalIgnoreCase) &&
            !string.Equals(updated.LogName, original.LogName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The event log '{updated.LogName}' is not available.");
        if (updated.EventIds is null || updated.Providers is null || updated.Levels is null || updated.ApplicationPools is null ||
            updated.EventIds.Any(id => id <= 0) || updated.CooldownSeconds is < 0 or > 86400 ||
            updated.Levels.Any(l => string.IsNullOrWhiteSpace(l) || !new[] { "Critical", "Error", "Warning", "Information", "Verbose" }.Contains(l, StringComparer.OrdinalIgnoreCase)) ||
            updated.Providers.Any(string.IsNullOrWhiteSpace) || updated.ApplicationPools.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("The proposed event IDs, levels, or cooldown are invalid.");
        if (changes.Any(change => string.Equals(change.Key, "messageRegex", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrEmpty(updated.MessageRegex))
        {
            try { _ = new System.Text.RegularExpressions.Regex(updated.MessageRegex); }
            catch (ArgumentException ex) { throw new InvalidOperationException("The proposed message regex is invalid.", ex); }
        }
        if (!updated.NotifyEmail && !updated.NotifySms)
            throw new InvalidOperationException("At least one notification channel must stay enabled.");
        return new RuleEditProposal(index, proposed.Description?.Trim() ?? "Edit rule", updated);
    }

    private static JsonNode BuildSchema() => JsonNode.Parse("""
    {"type":"object","properties":{
      "answer":{"type":"string"},
      "clarification":{"type":"string"},
      "rules":{"type":"array","items":{"type":"object","properties":{
        "name":{"type":"string"},"scenario":{"type":"string"},"levels":{"type":"array","items":{"type":"string"}},
        "applicationPools":{"type":"array","items":{"type":"string"}},"messageContains":{"type":"string"},
        "notifyEmail":{"type":"boolean"},"notifySms":{"type":"boolean"},"cooldownSeconds":{"type":"integer"},
        "logName":{"type":"string"},"providers":{"type":"array","items":{"type":"string"}},
        "eventIds":{"type":"array","items":{"type":"integer"}}},
        "required":["name","scenario","levels","applicationPools","messageContains","notifyEmail","notifySms","cooldownSeconds","logName","providers","eventIds"]}},
      "edit":{"type":"object","properties":{"ruleIndex":{"type":"integer"},"description":{"type":"string"},
        "changes":{"type":"object","additionalProperties":true}},"required":["ruleIndex","description","changes"]}
    },"required":["answer","clarification","rules"]}
    """)!;

    private sealed class ChatResponse
    {
        public string Answer { get; set; } = "";
        public string Clarification { get; set; } = "";
        public List<RuleDraft> Rules { get; set; } = [];
        public ChatEdit? Edit { get; set; }
        public DraftResult Draft => new() { Rules = Rules, Clarification = Clarification };
    }
    private sealed class ChatEdit
    {
        public int RuleIndex { get; set; }
        public string Description { get; set; } = "";
        public JsonNode? Changes { get; set; }
    }
}
