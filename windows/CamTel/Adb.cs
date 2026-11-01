using System.Diagnostics;

namespace CamTel;

/// <summary>Uses adb (if installed) to reach the phone over USB via port forwarding.</summary>
public static class Adb
{
    public static string? FindAdb()
    {
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            if (dir.Length > 0) candidates.Add(Path.Combine(dir.Trim('"'), "adb.exe"));
        foreach (var env in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var root = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "platform-tools", "adb.exe"));
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "platform-tools", "adb.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Serials of devices in the "device" state (authorized).</summary>
    public static async Task<List<(string Serial, string Model)>> DevicesAsync()
    {
        var result = new List<(string, string)>();
        var output = await RunAsync("devices -l");
        if (output == null) return result;
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[1] != "device") continue;
            var model = parts.FirstOrDefault(p => p.StartsWith("model:"))?[6..].Replace('_', ' ') ?? parts[0];
            result.Add((parts[0], model));
        }
        return result;
    }

    /// <summary>Forwards a local TCP port to the phone's port. Returns true on success.</summary>
    public static async Task<bool> ForwardAsync(string serial, int localPort, int remotePort)
    {
        var output = await RunAsync($"-s {serial} forward tcp:{localPort} tcp:{remotePort}");
        return output != null;
    }

    private static async Task<string?> RunAsync(string args)
    {
        var adb = FindAdb();
        if (adb == null) return null;
        try
        {
            var psi = new ProcessStartInfo(adb, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await p.WaitForExitAsync(cts.Token);
            await stderr;
            return p.ExitCode == 0 ? await stdout : null;
        }
        catch
        {
            return null;
        }
    }
}
