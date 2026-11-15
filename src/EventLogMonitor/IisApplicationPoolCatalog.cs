using System.Collections;
using System.Reflection;

namespace EventLogMonitor;

/// <summary>Reads IIS pools and live worker-process ownership without a compile-time IIS dependency.</summary>
public static class IisApplicationPoolCatalog
{
    public sealed record Snapshot(IReadOnlyList<string> ApplicationPools,
        IReadOnlyDictionary<int, string> WorkerProcesses, string? Error = null);

    public static Snapshot Read()
    {
        var assemblyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "inetsrv", "Microsoft.Web.Administration.dll");
        if (!File.Exists(assemblyPath))
            return new Snapshot([], new Dictionary<int, string>(), "IIS management components were not found.");

        object? manager = null;
        try
        {
            var assembly = Assembly.LoadFrom(assemblyPath);
            var managerType = assembly.GetType("Microsoft.Web.Administration.ServerManager", true)!;
            manager = Activator.CreateInstance(managerType)
                ?? throw new InvalidOperationException("Could not create IIS ServerManager.");

            var pools = new List<string>();
            if (managerType.GetProperty("ApplicationPools")?.GetValue(manager) is IEnumerable poolCollection)
            {
                foreach (var pool in poolCollection)
                {
                    var name = pool?.GetType().GetProperty("Name")?.GetValue(pool)?.ToString();
                    if (!string.IsNullOrWhiteSpace(name)) pools.Add(name);
                }
            }

            var workers = new Dictionary<int, string>();
            if (managerType.GetProperty("WorkerProcesses")?.GetValue(manager) is IEnumerable workerCollection)
            {
                foreach (var worker in workerCollection)
                {
                    if (worker is null) continue;
                    var type = worker.GetType();
                    var pidValue = type.GetProperty("ProcessId")?.GetValue(worker);
                    var poolName = type.GetProperty("AppPoolName")?.GetValue(worker)?.ToString();
                    if (pidValue is not null && int.TryParse(pidValue.ToString(), out var pid) &&
                        pid > 0 && !string.IsNullOrWhiteSpace(poolName)) workers[pid] = poolName;
                }
            }
            return new Snapshot(pools.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(), workers);
        }
        catch (Exception ex)
        {
            var message = ex is TargetInvocationException { InnerException: not null } tie
                ? tie.InnerException.Message : ex.Message;
            return new Snapshot([], new Dictionary<int, string>(), message);
        }
        finally { (manager as IDisposable)?.Dispose(); }
    }
}
