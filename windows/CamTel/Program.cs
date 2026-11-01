namespace CamTel;

internal static class Program
{
    public const string MutexName = @"Local\CamTel.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains("--install-driver")) return DriverInstaller.Install();
        if (args.Contains("--uninstall-driver")) return DriverInstaller.Uninstall();

        // A downloaded exe installs itself in %LOCALAPPDATA%\Programs\CamTel and relaunches from there.
        if (!AppInstaller.IsRunningInstalled && !args.Contains("--portable") && !System.Diagnostics.Debugger.IsAttached)
        {
            if (AppInstaller.InstallAndRelaunch(args.Append("--first-run").ToArray())) return 0;
        }

        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Already running: bring it forward instead of starting a second copy.
            if (EventWaitHandle.TryOpenExisting(MainForm.ShowEventName, out var show))
                using (show) show.Set();
            return 0;
        }

        Application.Run(new MainForm(startHidden: args.Contains("--minimized"), firstRun: args.Contains("--first-run")));
        return 0;
    }
}
