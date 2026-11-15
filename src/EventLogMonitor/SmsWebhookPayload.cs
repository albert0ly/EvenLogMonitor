namespace EventLogMonitor;

public sealed record SmsWebhookPayload(
    string Message,
    string Subject,
    string Rule,
    string Machine,
    string Log,
    int EventId,
    string Provider,
    string Time,
    IReadOnlyList<string> PhoneNumbers);
