using System.Text;
using System.Text.Json.Nodes;

namespace EventLogMonitor.Configurator.Ai;

public static class PromptBuilder
{
    private static readonly string[] LevelNames = ["Critical", "Error", "Warning", "Information", "Verbose"];

    /// <summary>JSON schema sent to Ollama so the reply always has this exact shape and allowed values.</summary>
    public static JsonNode BuildSchema()
    {
        var scenarios = string.Join(",", Enum.GetNames<Scenario>().Select(n => $"\"{n}\""));
        var levels = string.Join(",", LevelNames.Select(n => $"\"{n}\""));
        return JsonNode.Parse($$"""
        {
          "type": "object",
          "properties": {
            "clarification": { "type": "string" },
            "rules": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "scenario": { "type": "string", "enum": [{{scenarios}}] },
                  "levels": { "type": "array", "items": { "type": "string", "enum": [{{levels}}] } },
                  "applicationPools": { "type": "array", "items": { "type": "string" } },
                  "messageContains": { "type": "string" },
                  "notifyEmail": { "type": "boolean" },
                  "notifySms": { "type": "boolean" },
                  "cooldownSeconds": { "type": "integer" },
                  "logName": { "type": "string" },
                  "providers": { "type": "array", "items": { "type": "string" } },
                  "eventIds": { "type": "array", "items": { "type": "integer" } }
                },
                "required": ["name","scenario","levels","applicationPools","messageContains","notifyEmail","notifySms","cooldownSeconds","logName","providers","eventIds"]
              }
            }
          },
          "required": ["clarification", "rules"]
        }
        """)!;
    }

    public static IReadOnlyList<ChatTurn> BuildMessages(string userRequest, IReadOnlyList<string> pools, bool poolsKnown,
        RuleChatContext? context = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You help the user inspect, explain, and edit the current Notification Rules in EventLogMonitor Configurator, as well as draft new rules.");
        sb.AppendLine("Reply ONLY with JSON that matches the supplied schema.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- The current Notification Rules and selection are supplied below. Answer questions about them using those exact details. 'Last rule' means the final list item, not the selected item. Do NOT ask about applications or pools when asked to inspect a rule. The context is a snapshot of rules already saved in the rule list; unsaved rule-editor typing is not included.");
        sb.AppendLine("- For an edit request, return a short explanation in answer, an edit with a 1-based ruleIndex, description and changes object containing only changed fields; rules must be empty and clarification empty. Never invent a rule or change unrelated fields. If the target is ambiguous, ask which rule instead.");
        sb.AppendLine("- An edit may change name, enabled, logName, eventIds, providers, levels, messageContains, messageRegex, applicationPools, cooldownSeconds, notifyEmail or notifySms. Keep JSON value types consistent with the current rule. When changing filters, explain what will be matched.");
        sb.AppendLine("- For a new-rule request return rules and no edit. For a question return answer and empty rules with no edit. For a clarification return one question in clarification, empty rules and no edit.");
        sb.AppendLine("- For an inspection or explanation request, return an answer, no edit, and no new rules. Do not claim you can see unsaved editor changes or live application health.");
        sb.AppendLine("- Choose a scenario from the list below only when drafting NEW rules. Prefer a listed scenario over Custom.");
        sb.AppendLine("- For a listed scenario leave logName, providers and eventIds empty. Never invent event IDs; the tool fills them in.");
        sb.AppendLine("- Use Custom ONLY when the user clearly names a log, source or event ID (for example Security 4625). Then fill logName (Application, System, Security or an exact channel name), providers and eventIds. Do not guess.");
        sb.AppendLine("- applicationPools: use only names from the pool list, spelled exactly. Map an application or site name to its pool when the names clearly correspond. If the user says IIS Express, Visual Studio, localhost URL, or an ASP.NET Core/.NET 5+ app launched by Visual Studio, this is NOT full IIS: do not ask for or require an application pool and prefer AspNetCoreIisExpress.");
        sb.AppendLine("- IIS Express has no full-IIS/WAS application-pool lifecycle to monitor. Interpret 'IIS reset', 'restart', 'start/end/reset' for IIS Express as application/process crash or restart-related monitoring, and explain that the monitor can detect crashes/errors but cannot itself reset or restart IIS Express.");
        sb.AppendLine("- A localhost endpoint such as https://localhost:7166/ identifies the app but is not an event-log channel, provider, or pool name. Preserve it as context; do not repeatedly ask which pool it uses.");
        sb.AppendLine("- levels: leave empty unless the user explicitly restricts severity (for example 'only errors').");
        sb.AppendLine("- notifyEmail is true by default. notifySms is true only if the user mentions SMS or a text message. If the user asks only for SMS, set notifyEmail to false.");
        sb.AppendLine("- cooldownSeconds is 60 unless the user states another value.");
        sb.AppendLine("- messageContains: empty unless the user names a phrase.");
        sb.AppendLine("- If the request is ambiguous or cannot be expressed, return an empty rules array and ONE short question in clarification. Otherwise clarification is an empty string.");
        sb.AppendLine("- Previous assistant messages may contain questions. Use subsequent user answers as clarification of the original request; do not repeat an already answered question.");
        sb.AppendLine("- Several rules are allowed when several scenarios are needed. name is a short human-readable title.");
        sb.AppendLine();
        sb.AppendLine("Scenarios:");
        foreach (var (key, value) in ScenarioCatalog.Items)
            sb.AppendLine($"- {key}: {value.Description}");
        sb.AppendLine();
        sb.AppendLine(poolsKnown
            ? "IIS application pools on this server: " + (pools.Count == 0 ? "(none)" : string.Join(", ", pools.Select(p => $"\"{p}\"")))
            : "IIS application pools on this server: could not be read. Use the pool name exactly as the user wrote it.");
        sb.AppendLine();
        sb.AppendLine("Examples (pool names here are illustrative):");
        sb.AppendLine();
        sb.AppendLine("User: I have ASP.NET application 'Claims' in local IIS. I want to watch all critical errors and send notification when only IIS reset can help.");
        sb.AppendLine("""
        {"clarification":"","rules":[
        {"name":"Claims - IIS pool failure","scenario":"IisPoolFailure","levels":[],"applicationPools":["Claims"],"messageContains":"","notifyEmail":true,"notifySms":false,"cooldownSeconds":60,"logName":"","providers":[],"eventIds":[]},
        {"name":"Claims - IIS worker unresponsive","scenario":"IisWorkerUnresponsive","levels":[],"applicationPools":["Claims"],"messageContains":"","notifyEmail":true,"notifySms":false,"cooldownSeconds":60,"logName":"","providers":[],"eventIds":[]}]}
        """);
        sb.AppendLine();
        sb.AppendLine("User: Text me when someone fails to log on to this server.");
        sb.AppendLine("""
        {"clarification":"","rules":[
        {"name":"Failed logons","scenario":"Custom","levels":[],"applicationPools":[],"messageContains":"","notifyEmail":false,"notifySms":true,"cooldownSeconds":300,"logName":"Security","providers":["Microsoft-Windows-Security-Auditing"],"eventIds":[4625]}]}
        """);
        sb.AppendLine();
        sb.AppendLine("User: I have an ASP.NET Core .NET 10 app running in Visual Studio with IIS Express at https://localhost:7166/. Watch critical errors and tell me when IIS Express crashes or needs to be started again.");
        sb.AppendLine("""
        {"clarification":"","rules":[
        {"name":"ASP.NET Core IIS Express - .NET errors","scenario":"AspNetCoreIisExpress","levels":["Critical","Error"],"applicationPools":[],"messageContains":"","notifyEmail":true,"notifySms":false,"cooldownSeconds":60,"logName":"","providers":[],"eventIds":[]}]}
        """);
        sb.AppendLine();
        if (context is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Current configurator state (snapshot at chat open; not live Windows events):");
            sb.AppendLine(context.Describe());
            sb.AppendLine("Only change existing rules after the user explicitly requests an edit. The user must click Apply, then Save configuration.");
        }
        sb.AppendLine("User: Tell me when my app breaks.");
        sb.AppendLine("""
        {"clarification":"Which application (or IIS application pool) should I watch, and do you want web errors, crashes, or both?","rules":[]}
        """);

        return
        [
            new ChatTurn("system", sb.ToString()),
            new ChatTurn("user", userRequest.Trim())
        ];
    }
}
