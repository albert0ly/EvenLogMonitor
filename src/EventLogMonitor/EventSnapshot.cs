namespace EventLogMonitor;

public sealed record EventSnapshot(
    string Machine, string LogName, int EventId, string Provider, byte? Level,
    string LevelName, DateTime TimeCreated, long? RecordId, string Message,
    int? ProcessId, IReadOnlyList<string> ApplicationPoolNames);
