using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EventLogMonitor;

/// <summary>
/// Refreshes the live w3wp.exe PID-to-application-pool map from IIS. Recently exited
/// workers are retained briefly so crash events written immediately before exit can
/// still be attributed after the process disappears from IIS' live worker list.
/// </summary>
public sealed class IisWorkerProcessMap : BackgroundService
{
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(60);
    private readonly ILogger<IisWorkerProcessMap> _logger;
    private IReadOnlyDictionary<int, PoolEntry> _map = new Dictionary<int, PoolEntry>();

    public IisWorkerProcessMap(ILogger<IisWorkerProcessMap> logger) => _logger = logger;

    public string? FindPool(int? processId)
    {
        if (processId is null || processId <= 0) return null;
        var current = Volatile.Read(ref _map);
        if (current.TryGetValue(processId.Value, out var entry) &&
            DateTimeOffset.UtcNow - entry.LastSeenUtc <= Retention)
            return entry.PoolName;
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var snapshot = IisApplicationPoolCatalog.Read();
                var next = Volatile.Read(ref _map)
                    .Where(pair => now - pair.Value.LastSeenUtc <= Retention)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);

                foreach (var worker in snapshot.WorkerProcesses)
                    next[worker.Key] = new PoolEntry(worker.Value, now);

                Volatile.Write(ref _map, next);
                if (snapshot.Error is not null)
                    _logger.LogDebug("IIS worker-process mapping is unavailable: {Reason}", snapshot.Error);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not refresh IIS worker-process mapping."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private sealed record PoolEntry(string PoolName, DateTimeOffset LastSeenUtc);
}
