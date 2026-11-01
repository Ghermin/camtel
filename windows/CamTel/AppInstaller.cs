using System.Diagnostics;
using Microsoft.Win32;

namespace CamTel;

/// <summary>
/// Makes the downloaded exe "install itself": copies to %LOCALAPPDATA%\Programs\CamTel,
/// adds a Start menu shortcut and an optional "start with Windows" entry.
/// </summary>
public static class AppInstaller
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string QuitEventName = @"Local\CamTel.Quit";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CamTel");

    public static string InstalledExe => Path.Combine(InstallDir, "CamTel.exe");

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "CamTel.lnk");

    public static bool IsRunningInstalled =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(InstalledExe),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies this exe over the installed one (closing a running copy first) and starts it.
    /// Returns false if the copy failed, in which case the caller keeps running from here.
    /// </summary>
    public static bool InstallAndRelaunch(string[] args)
    {
        try
        {
            AskRunningInstanceToQuit();
            Directory.CreateDirectory(InstallDir);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(250);
                }
            }
            CreateShortcut();
            Process.Start(new ProcessStartInfo(InstalledExe, string.Join(' ', args)) { UseShellExecute = false });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AskRunningInstanceToQuit()
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var quit)) return;
        using (quit) quit.Set();
        // Wait for the other instance to release the single-instance mutex.
        using var mutex = new Mutex(false, Program.MutexName);
        try
        {
            if (mutex.WaitOne(TimeSpan.FromSeconds(8))) mutex.ReleaseMutex();
        }
        catch (AbandonedMutexException)
        {
        }
    }

    private static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(ShortcutPath);
        link.TargetPath = InstalledExe;
        link.WorkingDirectory = InstallDir;
        link.Description = "CamTel - ton téléphone comme webcam";
        link.Save();
    }

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("CamTel") != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("CamTel", $"\"{InstalledExe}\" --minimized");
            else key.DeleteValue("CamTel", throwOnMissingValue: false);
        }
    }

    /// <summary>Removes shortcut, autostart and (after exit) the installed files.</summary>
    public static void Uninstall()
    {
        StartWithWindows = false;
        try { File.Delete(ShortcutPath); } catch { }
        var dir = InstallDir;
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CamTel");
        var tools = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CamTel");
        // Delete once this process has exited.
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{dir}\" & rmdir /s /q \"{appData}\" & rmdir /s /q \"{tools}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
