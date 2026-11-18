using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Security;
using System.Text.RegularExpressions;
using EventLogMonitor;

namespace EventLogMonitor.Configurator;

internal static class TestEventWriter
{
    internal const string Marker = "For test purposes. Not real event";
    private const string DefaultSource = "EventLogMonitor.Test";

    internal static string Write(EventRule rule)
    {
        if (!rule.Enabled) throw new InvalidOperationException("The selected rule is disabled. Enable and save it before testing.");

        var log = rule.LogName == "*" || string.IsNullOrWhiteSpace(rule.LogName) ? "Application" : rule.LogName;
        var source = rule.Providers.FirstOrDefault() ?? DefaultSource;
        var id = rule.EventIds.Count == 0 ? 1000 : rule.EventIds.FirstOrDefault(value => value is >= 0 and <= 65535);
        if (id == 0 && !rule.EventIds.Contains(0) && rule.EventIds.Count > 0)
            throw new InvalidOperationException("Classic Windows Event Log writes support Event IDs from 0 to 65535. This rule needs an ID in that range.");

        var type = ChooseType(rule.Levels);
        var message = BuildMessage(rule);
        try
        {
            if (!MatchesMessage(rule, message))
                throw new InvalidOperationException("The generated message does not satisfy the rule's message regex. Use a compatible regex or a separate dedicated test-source rule.");
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("The selected rule contains an invalid message regex; the monitor cannot match this rule.", ex);
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new InvalidOperationException("The selected rule's message regex timed out on the generated text; no event was written.", ex);
        }

        try
        {
            if (!EventLog.SourceExists(source))
            {
                if (rule.Providers.Count > 0)
                    throw new InvalidOperationException($"Source '{source}' is not registered. Registering a new classic source under an existing provider name would not reproduce that provider's events.");
                EventLog.CreateEventSource(new EventSourceCreationData(source, log));
                return $"Registered new source '{source}' for '{log}'. Windows may need time to activate it; click Create Test Event again after the source becomes available. No event was written.";
            }

            var actualLog = EventLog.LogNameFromSourceName(source, ".");
            if (!string.Equals(actualLog, log, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Source '{source}' is registered for '{actualLog}', not '{log}'. Windows sources cannot be redirected to another log.");

            var before = FindNewestRecordId(log);
            EventLog.WriteEntry(source, message, type, id);
            var written = FindNewEvent(log, source, id, before, message);
            if (written is null)
                return $"Windows accepted the write to '{log}' from '{source}', but the event could not be verified in the log. Check Event Viewer and the monitor service diagnostics; notification is not confirmed.";
            using (written)
            {
                string actualMessage;
                try { actualMessage = written.FormatDescription() ?? written.ToXml(); }
                catch (EventLogException) { actualMessage = written.ToXml(); }
                if (!MatchesMessage(rule, actualMessage) || !MatchesLevel(rule.Levels, written.Level, written.LevelDisplayName))
                    return $"Event {id} was written to '{log}' from '{source}', but its rendered message or level does not match the rule. Check Event Viewer; notification is not confirmed.";
            }
            return $"Wrote and verified Event ID {id} from '{source}' in '{log}'. Check the running monitor's diagnostics and notification destination; delivery is not confirmed.";
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or EventLogException)
        {
            throw new InvalidOperationException($"Windows could not write a matching test event from '{source}' to '{log}': {ex.Message}\r\n\r\nThe source may be protected, already registered to another log, or require Administrator privileges. To test delivery without impersonating this provider, add a separate enabled rule for source '{DefaultSource}' in the Application log, save configuration, restart the monitor, and try that rule.", ex);
        }
    }

    private static EventLogEntryType ChooseType(List<string> levels)
    {
        if (levels.Count == 0) return EventLogEntryType.Information;
        foreach (var level in levels)
        {
            if (level.Equals("Error", StringComparison.OrdinalIgnoreCase)) return EventLogEntryType.Error;
            if (level.Equals("Warning", StringComparison.OrdinalIgnoreCase) || level.Equals("Warn", StringComparison.OrdinalIgnoreCase)) return EventLogEntryType.Warning;
            if (level.Equals("Information", StringComparison.OrdinalIgnoreCase) || level.Equals("Informational", StringComparison.OrdinalIgnoreCase) || level.Equals("Info", StringComparison.OrdinalIgnoreCase)) return EventLogEntryType.Information;
        }
        throw new InvalidOperationException("Classic Event Log writes support Error, Warning and Information, but not Critical or Verbose. Use a separate test-source rule with a supported level.");
    }

    private static bool MatchesLevel(List<string> levels, byte? level, string? displayName) => levels.Count == 0 || levels.Any(value =>
        value.Equals(displayName, StringComparison.OrdinalIgnoreCase) || level switch
        {
            2 => value.Equals("Error", StringComparison.OrdinalIgnoreCase),
            3 => value.Equals("Warning", StringComparison.OrdinalIgnoreCase) || value.Equals("Warn", StringComparison.OrdinalIgnoreCase),
            4 => value.Equals("Information", StringComparison.OrdinalIgnoreCase) || value.Equals("Informational", StringComparison.OrdinalIgnoreCase) || value.Equals("Info", StringComparison.OrdinalIgnoreCase),
            _ => false
        });

    private static string BuildMessage(EventRule rule)
    {
        var lines = new List<string> { $"Test notification for rule '{rule.Name}'." };
        if (!string.IsNullOrWhiteSpace(rule.MessageContains)) lines.Add(rule.MessageContains);
        if (rule.ApplicationPools.Count > 0) lines.Add($"Application pool '{rule.ApplicationPools[0]}' has encountered a test condition.");
        lines.Add(Marker);
        return string.Join(Environment.NewLine, lines);
    }

    private static bool MatchesMessage(EventRule rule, string message)
    {
        if (!string.IsNullOrWhiteSpace(rule.MessageContains) && !message.Contains(rule.MessageContains, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(rule.MessageRegex)) return true;
        return Regex.IsMatch(message, rule.MessageRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static long FindNewestRecordId(string log)
    {
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, "*") { ReverseDirection = true });
        using var record = reader.ReadEvent();
        return record?.RecordId ?? 0;
    }

    private static EventRecord? FindNewEvent(string log, string source, int id, long after, string message)
    {
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, "*") { ReverseDirection = true });
        for (var i = 0; i < 200; i++)
        {
            var record = reader.ReadEvent();
            if (record is null) break;
            if ((record.RecordId ?? 0) <= after && after > 0) { record.Dispose(); break; }
            if ((record.RecordId ?? 0) > after && record.Id == id &&
                string.Equals(record.ProviderName, source, StringComparison.OrdinalIgnoreCase))
            {
                string actual;
                try { actual = record.FormatDescription() ?? ""; }
                catch (EventLogException) { actual = record.ToXml(); }
                if (actual.Contains(message, StringComparison.Ordinal)) return record;
            }
            record.Dispose();
        }
        return null;
    }
}
