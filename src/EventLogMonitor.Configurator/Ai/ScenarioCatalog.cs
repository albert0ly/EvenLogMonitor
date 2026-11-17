namespace EventLogMonitor.Configurator.Ai;

/// <summary>Scenarios the model may choose from. The model never writes Event IDs for these;
/// it picks a scenario and this catalog supplies the log, providers and IDs.
/// Edit this file to add or adjust scenarios, then add the key to the Scenario enum.</summary>
public enum Scenario
{
    IisPoolFailure,
    IisWorkerUnresponsive,
    AspNetUnhandledException,
    DotNetRuntimeCrash,
    W3wpCrash,
    AspNetCoreIisExpress,
    Custom
}

public sealed record CatalogEntry(string LogName, string[] Providers, int[] EventIds,
    string[] DefaultLevels, string MessageContains = "");

public static class ScenarioCatalog
{
    public static readonly IReadOnlyDictionary<Scenario, (string Description, CatalogEntry[] Entries)> Items =
        new Dictionary<Scenario, (string, CatalogEntry[])>
        {
            [Scenario.IisPoolFailure] = (
                "IIS application pool was disabled by WAS (rapid-fail protection, failed worker start, invalid identity). " +
                "The site stays down until the pool is started or IIS is restarted. Use for: 'IIS reset needed', 'site is down', 'pool stopped'.",
                [new("System", ["Microsoft-Windows-WAS"], [5002, 5057, 5059], ["Error"])]),

            [Scenario.IisWorkerUnresponsive] = (
                "IIS worker process (w3wp) stopped responding to WAS pings or lost communication with WAS. " +
                "Typically needs a pool recycle or IIS reset. Use for: 'hangs', 'not responding', 'frozen'.",
                [new("System", ["Microsoft-Windows-WAS"], [5010, 5011], ["Error", "Warning"])]),

            [Scenario.AspNetUnhandledException] = (
                "ASP.NET (.NET Framework 4.x) unhandled exception / application error in the Application log. " +
                "Usually does NOT need an IIS reset. Use for: 'application errors', 'exceptions in the web app'.",
                [new("Application", ["ASP.NET 4.0.30319.0", "ASP.NET 2.0.50727.0"], [1309], ["Error", "Warning"])]),

            [Scenario.DotNetRuntimeCrash] = (
                ".NET Framework runtime unhandled exception that crashed a process (console apps, services). " +
                "Use for: 'console app crashed', 'unhandled exception in service'.",
                [new("Application", [".NET Runtime"], [1026], ["Error"])]),

            [Scenario.W3wpCrash] = (
                "Windows Application Error reports for the IIS worker process w3wp.exe (process crash).",
                [new("Application", [], [1000, 1001], [], "w3wp.exe")]),

            [Scenario.AspNetCoreIisExpress] = (
                "ASP.NET Core application launched by Visual Studio using IIS Express. The https://localhost:<port> URL is not an IIS application pool and IIS Express does not produce the full IIS/WAS pool events. Monitor application crashes and .NET runtime errors instead; do not ask for a pool name.",
                [new("Application", [".NET Runtime"], [1026], ["Error"]), new("Application", [], [1000, 1001], [], "iisexpress.exe")]),

            [Scenario.Custom] = (
                "Anything not covered above. The model must then fill logName, providers and eventIds itself " +
                "and only when the user clearly names them (for example Security 4625 failed logons).",
                [])
        };
}
