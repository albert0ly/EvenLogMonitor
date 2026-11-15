namespace EventLogMonitor;

public sealed class EventLogMonitorOptions
{
    // Standard Windows logs are included. Add custom channel names here as needed.
    public List<string> Logs { get; set; } = ["Application", "System", "Security"];
    public List<EventRule> Rules { get; set; } = [];
}

public sealed class EventRule
{
    public string Name { get; set; } = "Unnamed rule";
    public bool Enabled { get; set; } = true;

    // Exact log name, or "*" to match any configured log.
    public string LogName { get; set; } = "*";

    // Empty lists mean "any".
    public List<int> EventIds { get; set; } = [];
    public List<string> Providers { get; set; } = [];
    public List<string> Levels { get; set; } = [];

    // Simple, case-insensitive substring match against the rendered event message.
    public string MessageContains { get; set; } = "";

    // Advanced .NET regular expression, retained for power users.
    public string MessageRegex { get; set; } = "";

    // Optional IIS application-pool names. Empty means no pool filter.
    public List<string> ApplicationPools { get; set; } = [];

    // Suppress repeated notifications for this rule for this many seconds. 0 disables suppression.
    public int CooldownSeconds { get; set; } = 0;

    public bool NotifyEmail { get; set; } = true;
    public bool NotifySms { get; set; } = false;
}

public sealed class EmailNotificationOptions
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    // EnableSsl uses SMTP STARTTLS. It is not implicit TLS on port 465.
    public bool EnableSsl { get; set; } = true;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public List<string> To { get; set; } = [];
}

public sealed class SmsWebhookOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string Method { get; set; } = "POST";
    public string ContentType { get; set; } = "application/json";

    // Phone numbers are sent as part of the webhook request body.
    public List<string> PhoneNumbers { get; set; } = [];

    // Use JSON string/value placeholders.
    // Available placeholders: {{message}}, {{subject}}, {{machine}}, {{log}},
    // {{eventId}}, {{provider}}, {{time}}, {{rule}}, {{phoneNumbersJson}}.
    public string BodyTemplate { get; set; } = "{\"message\":{{message}},\"phoneNumbers\":{{phoneNumbersJson}},\"subject\":{{subject}},\"rule\":{{rule}},\"machine\":{{machine}},\"log\":{{log}},\"eventId\":{{eventId}},\"provider\":{{provider}},\"time\":{{time}}}";

    // Example: { "Authorization": "Bearer ..." }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
