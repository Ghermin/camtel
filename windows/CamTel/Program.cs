namespace CamTel;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains("--install-driver")) return DriverInstaller.Install();
        if (args.Contains("--uninstall-driver")) return DriverInstaller.Uninstall();

        using var mutex = new Mutex(true, @"Local\CamTel.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("CamTel est déjà ouvert (regarde dans la barre des tâches ou près de l'horloge).", "CamTel",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.Run(new MainForm());
        return 0;
    }
}
