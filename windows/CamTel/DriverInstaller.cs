using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace CamTel;

/// <summary>
/// Installs the "CamTel Camera" DirectShow virtual camera (a rebranded build of softcam)
/// by extracting the embedded DLLs to %ProgramData%\CamTel\driver and registering them.
/// </summary>
public static class DriverInstaller
{
    public const string Clsid = "{61A0B261-FCBA-4EF9-824C-A9C6D2639911}";

    public static string DriverRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CamTel", "driver");

    public static string Dll64Path => Path.Combine(DriverRoot, "x64", "softcam.dll");
    public static string Dll32Path => Path.Combine(DriverRoot, "x86", "softcam.dll");

    /// <summary>Path of the registered 64-bit DLL, or null if the camera is not registered.</summary>
    public static string? RegisteredDll64()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64)
            .OpenSubKey($@"CLSID\{Clsid}\InprocServer32");
        var path = key?.GetValue(null) as string;
        return path != null && File.Exists(path) ? path : null;
    }

    public static bool IsInstalled => RegisteredDll64() != null;

    /// <summary>
    /// Bump when the driver sources in /driver change. Compared with the installed marker so an
    /// app update only asks for admin rights when the driver itself changed.
    /// </summary>
    public const string DriverVersion = "1";

    private static string VersionFile => Path.Combine(DriverRoot, "version.txt");

    public static bool NeedsUpdate()
    {
        if (RegisteredDll64() == null) return true;
        try
        {
            return !File.Exists(VersionFile) || File.ReadAllText(VersionFile).Trim() != DriverVersion;
        }
        catch
        {
            return false;
        }
    }

    public static bool HasEmbeddedDriver()
    {
        using var res = OpenResource("softcam64.dll");
        return res != null;
    }

    /// <summary>Relaunches this exe elevated to install or uninstall. Returns true on success.</summary>
    public static bool RunElevated(bool install)
    {
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var psi = new ProcessStartInfo(exe, install ? "--install-driver" : "--uninstall-driver")
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User refused the UAC prompt.
            return false;
        }
    }

    /// <summary>Runs in the elevated process. Returns a process exit code.</summary>
    public static int Install()
    {
        try
        {
            Extract("softcam64.dll", Dll64Path);
            Extract("softcam32.dll", Dll32Path);
            var ok = RegSvr32(Dll64Path, wow64: false, unregister: false);
            if (File.Exists(Dll32Path))
                ok &= RegSvr32(Dll32Path, wow64: true, unregister: false);
            if (ok) File.WriteAllText(VersionFile, DriverVersion);
            return ok ? 0 : 2;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Installation de la caméra virtuelle impossible :\n" + ex.Message, "CamTel",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>Runs in the elevated process. Returns a process exit code.</summary>
    public static int Uninstall()
    {
        var ok = true;
        if (File.Exists(Dll64Path)) ok &= RegSvr32(Dll64Path, wow64: false, unregister: true);
        if (File.Exists(Dll32Path)) ok &= RegSvr32(Dll32Path, wow64: true, unregister: true);
        try
        {
            Directory.Delete(DriverRoot, recursive: true);
        }
        catch
        {
            // Files may still be loaded by a camera app; they are harmless once unregistered.
        }
        return ok ? 0 : 2;
    }

    private static void Extract(string resource, string target)
    {
        using var res = OpenResource(resource) ?? throw new InvalidOperationException($"{resource} absent de l'exe");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            if (SameContent(target, res)) return;
            // The DLL may be loaded by a running camera app: move it aside instead of overwriting.
            var old = target + "." + DateTime.Now.Ticks + ".old";
            File.Move(target, old);
            try { File.Delete(old); } catch { /* deleted at next install */ }
        }
        res.Position = 0;
        using var fs = File.Create(target);
        res.CopyTo(fs);
        foreach (var stale in Directory.GetFiles(Path.GetDirectoryName(target)!, "*.old"))
        {
            try { File.Delete(stale); } catch { }
        }
    }

    private static bool RegSvr32(string dll, bool wow64, bool unregister)
    {
        var sysDir = wow64
            ? Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)
            : Environment.GetFolderPath(Environment.SpecialFolder.System);
        var psi = new ProcessStartInfo(Path.Combine(sysDir, "regsvr32.exe"),
            (unregister ? "/u " : "") + "/s \"" + dll + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    private static Stream? OpenResource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

    private static bool SameContent(string file, Stream res)
    {
        res.Position = 0;
        using var fs = File.OpenRead(file);
        if (fs.Length != res.Length) return false;
        var a = new byte[81920];
        var b = new byte[81920];
        while (true)
        {
            var n = fs.Read(a, 0, a.Length);
            if (n == 0) return true;
            res.ReadExactly(b, 0, n);
            if (!a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n))) return false;
        }
    }
}
