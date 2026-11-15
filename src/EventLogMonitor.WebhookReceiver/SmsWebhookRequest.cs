namespace EventLogMonitor.WebhookReceiver;

public sealed class SmsWebhookRequest
{
    public string Message { get; set; } = string.Empty;
    public List<string> PhoneNumbers { get; set; } = [];
    public string Subject { get; set; } = string.Empty;
    public string Rule { get; set; } = string.Empty;
    public string Machine { get; set; } = string.Empty;
    public string Log { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Time { get; set; } = string.Empty;
}
