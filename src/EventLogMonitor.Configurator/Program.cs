using System.Windows.Forms;

namespace EventLogMonitor.Configurator;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var configPath = args.Length > 0 ? args[0] : null;
        Application.Run(new MainForm(configPath));
    }
}
