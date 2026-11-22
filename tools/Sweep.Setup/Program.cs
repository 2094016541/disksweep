using System.Windows.Forms;

namespace Sweep.Setup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        bool uninstall = args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase))
            || (Environment.ProcessPath is { } path
                && Path.GetFileNameWithoutExtension(path).Contains("Uninstall", StringComparison.OrdinalIgnoreCase));

        Application.Run(uninstall ? new UninstallForm() : new InstallerForm());
    }
}
