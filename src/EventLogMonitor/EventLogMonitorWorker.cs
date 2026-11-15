using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventLogMonitor;

public sealed class EventLogMonitorWorker : BackgroundService
{
    private readonly EventLogMonitorOptions _options;
    private readonly NotificationDispatcher _notifications;
    private readonly ILogger<EventLogMonitorWorker> _logger;
    private readonly IisWorkerProcessMap _iisWorkerProcesses;
    private readonly Channel<EventSnapshot> _events = Channel.CreateUnbounded<EventSnapshot>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly List<EventLogWatcher> _watchers = [];
    private readonly List<CompiledRule> _rules = [];
    private readonly Dictionary<string, DateTimeOffset> _lastNotified = new(StringComparer.OrdinalIgnoreCase);

    public EventLogMonitorWorker(
        IOptions<EventLogMonitorOptions> options,
        NotificationDispatcher notifications,
        IisWorkerProcessMap iisWorkerProcesses,
        ILogger<EventLogMonitorWorker> logger)
    {
        _options = options.Value;
        _notifications = notifications;
        _iisWorkerProcesses = iisWorkerProcesses;
        _logger = logger;

        foreach (var rule in _options.Rules.Where(r => r.Enabled))
        {
            try
            {
                Regex? regex = string.IsNullOrWhiteSpace(rule.MessageRegex)
                    ? null
                    : new Regex(rule.MessageRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1));
                _rules.Add(new CompiledRule(rule, regex));
            }
            catch (ArgumentException ex)
            {
                _logger.LogError(ex, "Rule '{RuleName}' has an invalid MessageRegex and will be ignored.", rule.Name);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_rules.Count == 0)
            _logger.LogWarning("No enabled rules are configured. Add rules in appsettings.json.");

        var logNames = _options.Logs
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var logName in logNames)
        {
            try
            {
                var query = new EventLogQuery(logName, PathType.LogName, "*");
                // readExistingEvents=false means only events written after service startup are handled.
                var watcher = new EventLogWatcher(query, null, readExistingEvents: false);
                watcher.EventRecordWritten += OnEventRecordWritten;
                watcher.Enabled = true;
                _watchers.Add(watcher);
                _logger.LogInformation("Monitoring Windows Event Log '{LogName}'.", logName);
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogError(ex, "Could not subscribe to Windows Event Log '{LogName}'.", logName);
            }
        }

        if (_watchers.Count == 0)
            _logger.LogError("No Windows Event Logs could be opened. Check log names and service permissions.");

        try
        {
            await foreach (var item in _events.Reader.ReadAllAsync(stoppingToken))
                await ProcessEventAsync(item, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
    }

    private void OnEventRecordWritten(object? sender, EventRecordWrittenEventArgs args)
    {
        if (args.EventException is not null)
        {
            _logger.LogWarning(args.EventException, "An error occurred while reading a Windows Event Log.");
            return;
        }

        using var record = args.EventRecord;
        if (record is null)
            return;

        try
        {
            var eventXml = record.ToXml();
            string message;
            try
            {
                message = record.FormatDescription() ?? "";
            }
            catch (EventLogException)
            {
                // Some providers do not have message resources available on this machine.
                message = eventXml;
            }

            var processId = ReadProcessId(eventXml);
            var poolNames = ReadApplicationPoolNames(eventXml, message).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mappedPool = _iisWorkerProcesses.FindPool(processId);
            if (!string.IsNullOrWhiteSpace(mappedPool)) poolNames.Add(mappedPool);

            var snapshot = new EventSnapshot(
                Machine: string.IsNullOrWhiteSpace(record.MachineName) ? Environment.MachineName : record.MachineName,
                LogName: record.LogName ?? "Unknown",
                EventId: record.Id,
                Provider: record.ProviderName ?? "Unknown",
                Level: record.Level,
                LevelName: record.LevelDisplayName ?? GetLevelName(record.Level),
                TimeCreated: record.TimeCreated ?? DateTime.Now,
                RecordId: record.RecordId,
                Message: message,
                ProcessId: processId,
                ApplicationPoolNames: poolNames.ToArray());

            if (!_events.Writer.TryWrite(snapshot))
                _logger.LogWarning("Could not queue event {EventId} from {LogName}.", snapshot.EventId, snapshot.LogName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read an event record from Windows Event Log.");
        }
    }

    private async Task ProcessEventAsync(EventSnapshot item, CancellationToken cancellationToken)
    {
        foreach (var compiled in _rules)
        {
            bool matched;
            try
            {
                matched = Matches(compiled, item);
            }
            catch (RegexMatchTimeoutException ex)
            {
                _logger.LogWarning(ex, "MessageRegex timed out for rule '{RuleName}' on Event ID {EventId}; event ignored for this rule.",
                    compiled.Rule.Name, item.EventId);
                continue;
            }

            if (!matched)
                continue;

            var rule = compiled.Rule;
            var now = DateTimeOffset.UtcNow;
            if (rule.CooldownSeconds > 0 &&
                _lastNotified.TryGetValue(rule.Name, out var previous) &&
                now - previous < TimeSpan.FromSeconds(rule.CooldownSeconds))
            {
                _logger.LogDebug("Suppressed event {EventId} for rule '{RuleName}' by cooldown.", item.EventId, rule.Name);
                continue;
            }

            _logger.LogWarning(
                "Rule '{RuleName}' matched: {Machine}/{LogName}, Event ID {EventId}, Provider {Provider}.",
                rule.Name, item.Machine, item.LogName, item.EventId, item.Provider);

            var sent = await _notifications.SendAsync(rule, item, cancellationToken);
            if (sent && rule.CooldownSeconds > 0)
                _lastNotified[rule.Name] = now;
        }
    }

    private static bool Matches(CompiledRule compiled, EventSnapshot item)
    {
        var rule = compiled.Rule;

        if (!string.IsNullOrWhiteSpace(rule.LogName) && rule.LogName != "*" &&
            !string.Equals(rule.LogName, item.LogName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.EventIds.Count > 0 && !rule.EventIds.Contains(item.EventId))
            return false;

        if (rule.Providers.Count > 0 && !rule.Providers.Any(p =>
                string.Equals(p, item.Provider, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (rule.Levels.Count > 0 && !rule.Levels.Any(level => LevelMatches(level, item)))
            return false;

        if (!string.IsNullOrWhiteSpace(rule.MessageContains) &&
            !item.Message.Contains(rule.MessageContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (compiled.MessageRegex is not null && !compiled.MessageRegex.IsMatch(item.Message))
            return false;

        if (rule.ApplicationPools.Count > 0 && !rule.ApplicationPools.Any(selected =>
                item.ApplicationPoolNames.Any(actual => string.Equals(actual, selected, StringComparison.OrdinalIgnoreCase))))
            return false;

        return true;
    }

    private static bool LevelMatches(string configured, EventSnapshot item)
    {
        if (string.Equals(configured, item.LevelName, StringComparison.OrdinalIgnoreCase))
            return true;

        // Windows event level numbers: 1=Critical, 2=Error, 3=Warning, 4=Information, 5=Verbose.
        var expected = configured.Trim().ToLowerInvariant() switch
        {
            "critical" or "crit" => (byte)1,
            "error" => (byte)2,
            "warning" or "warn" => (byte)3,
            "information" or "informational" or "info" => (byte)4,
            "verbose" or "debug" => (byte)5,
            _ => (byte)0
        };

        return expected != 0 && item.Level == expected;
    }

    private static int? ReadProcessId(string eventXml)
    {
        try
        {
            var document = XDocument.Parse(eventXml);

            // Prefer event payload fields (for example FaultingProcessId in Application Error 1000).
            // System/Execution ProcessID can identify the process that logged the event, not the
            // process that actually faulted, so use it only as a fallback.
            foreach (var element in document.Descendants())
            {
                var fieldName = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value
                    ?? element.Name.LocalName;
                if (!fieldName.Contains("processid", StringComparison.OrdinalIgnoreCase)) continue;
                var parsed = ParseProcessId(element.Value);
                if (parsed is not null) return parsed;
            }

            var execution = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Execution");
            var executionValue = execution?.Attributes().FirstOrDefault(a => a.Name.LocalName == "ProcessID")?.Value;
            return ParseProcessId(executionValue);
        }
        catch { return null; }
    }

    private static int? ParseProcessId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        if (int.TryParse(candidate, out var pid) && pid > 0) return pid;
        if (candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) candidate = candidate[2..];
        return int.TryParse(candidate, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out pid) && pid > 0 ? pid : null;
    }

    private static IEnumerable<string> ReadApplicationPoolNames(string eventXml, string message)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var document = XDocument.Parse(eventXml);
            foreach (var element in document.Descendants())
            {
                var fieldName = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value
                    ?? element.Name.LocalName;
                var isPoolName = fieldName.Contains("poolname", StringComparison.OrdinalIgnoreCase) ||
                    fieldName.Equals("pool", StringComparison.OrdinalIgnoreCase) ||
                    fieldName.Equals("apppool", StringComparison.OrdinalIgnoreCase);
                if (isPoolName && !string.IsNullOrWhiteSpace(element.Value))
                    names.Add(element.Value.Trim());
            }
        }
        catch { /* Continue with the rendered message. */ }

        // WAS commonly renders messages such as: "A process serving application pool PoolName ...".
        var patterns = new[]
        {
            "\\bapplication\\s+pool\\s+['\\\"“](?<pool>[^'\\\"”\\r\\n]+)['\\\"”]",
            "\\bapplication\\s+pool\\s+(?<pool>[A-Za-z0-9_.()$ -]+?)\\s+(?:has|is|was|could|cannot|failed|reached|requested|suffered|encountered|terminated|stopped|disabled)\\b"
        };
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                var candidate = match.Groups["pool"].Value.Trim().TrimEnd('.', ',', ';', ':');
                if (!string.IsNullOrWhiteSpace(candidate)) names.Add(candidate);
            }
        }
        return names;
    }

    private static string GetLevelName(byte? level) => level switch
    {
        1 => "Critical",
        2 => "Error",
        3 => "Warning",
        4 => "Information",
        5 => "Verbose",
        _ => "Unknown"
    };

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.Enabled = false;
                watcher.EventRecordWritten -= OnEventRecordWritten;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while disposing an Event Log subscription.");
            }
        }
        _events.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private sealed record CompiledRule(EventRule Rule, Regex? MessageRegex);
}
