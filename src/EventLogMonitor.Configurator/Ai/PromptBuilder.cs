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

    public static IReadOnlyList<ChatTurn> BuildMessages(string userRequest, IReadOnlyList<string> pools, bool poolsKnown)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You convert a plain-English request into Windows Event Log monitoring rules for the EventLogMonitor tool.");
        sb.AppendLine("Reply ONLY with JSON that matches the supplied schema.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Choose a scenario from the list below. Prefer a listed scenario over Custom.");
        sb.AppendLine("- For a listed scenario leave logName, providers and eventIds empty. Never invent event IDs; the tool fills them in.");
        sb.AppendLine("- Use Custom ONLY when the user clearly names a log, source or event ID (for example Security 4625). Then fill logName (Application, System, Security or an exact channel name), providers and eventIds. Do not guess.");
        sb.AppendLine("- applicationPools: use only names from the pool list, spelled exactly. Map an application or site name to its pool when the names clearly correspond. If you cannot tell which pool is meant, return no rules and ask in clarification.");
        sb.AppendLine("- levels: leave empty unless the user explicitly restricts severity (for example 'only errors').");
        sb.AppendLine("- notifyEmail is true by default. notifySms is true only if the user mentions SMS or a text message. If the user asks only for SMS, set notifyEmail to false.");
        sb.AppendLine("- cooldownSeconds is 60 unless the user states another value.");
        sb.AppendLine("- messageContains: empty unless the user names a phrase.");
        sb.AppendLine("- If the request is ambiguous or cannot be expressed, return an empty rules array and ONE short question in clarification. Otherwise clarification is an empty string.");
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
