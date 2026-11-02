using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;

namespace CamTel;

/// <summary>Thin wrapper around adb. Downloads Google's platform-tools on first use if needed.</summary>
public static class Adb
{
    private const string PlatformToolsUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";

    private static string ToolsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CamTel");

    private static readonly SemaphoreSlim DownloadLock = new(1);
    private static string? _adb;

    public static string? FindAdb()
    {
        if (_adb != null && File.Exists(_adb)) return _adb;
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
        candidates.Add(Path.Combine(ToolsRoot, "platform-tools", "adb.exe"));
        _adb = candidates.FirstOrDefault(File.Exists);
        return _adb;
    }

    /// <summary>Returns adb, downloading platform-tools from Google into %LOCALAPPDATA%\CamTel if missing.</summary>
    /// <summary>True when the adb server was not running before CamTel needed it.</summary>
    public static bool StartedServer { get; private set; }

    private static bool ServerRunning()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", 5037).Wait(300) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Stops the adb server if CamTel started it, so nothing is left running on exit.</summary>
    public static void StopServerIfOwned()
    {
        var adb = FindAdb();
        if (adb == null || !StartedServer) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(adb, "kill-server") { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(3000);
        }
        catch
        {
        }
    }

    public static async Task<string?> EnsureAsync(Action<string> status, CancellationToken ct)
    {
        StartedServer = !ServerRunning();
        if (FindAdb() is { } found) return found;
        await DownloadLock.WaitAsync(ct);
        try
        {
            if (FindAdb() is { } again) return again;
            status("Préparation du mode USB (téléchargement des outils Android de Google)…");
            var zip = Path.Combine(Path.GetTempPath(), "camtel-platform-tools.zip");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            await using (var src = await http.GetStreamAsync(PlatformToolsUrl, ct))
            await using (var dst = File.Create(zip))
            {
                await src.CopyToAsync(dst, ct);
            }
            Directory.CreateDirectory(ToolsRoot);
            var target = Path.Combine(ToolsRoot, "platform-tools");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            ZipFile.ExtractToDirectory(zip, ToolsRoot);
            File.Delete(zip);
            return FindAdb();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            DownloadLock.Release();
        }
    }

    public sealed record Device(string Serial, string State, string Model);

    /// <summary>All attached devices, including unauthorized ones.</summary>
    public static async Task<List<Device>> DevicesAsync()
    {
        var result = new List<Device>();
        var output = await RunAsync("devices -l");
        if (output == null) return result;
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var model = parts.FirstOrDefault(p => p.StartsWith("model:"))?[6..].Replace('_', ' ') ?? "Android";
            result.Add(new Device(parts[0], parts[1], model));
        }
        return result;
    }

    public static Task<string?> ShellAsync(string serial, string command) =>
        RunAsync($"-s {serial} shell {command}");

    public static async Task<bool> ForwardAsync(string serial, int localPort, int remotePort) =>
        await RunAsync($"-s {serial} forward tcp:{localPort} tcp:{remotePort}") != null;

    /// <summary>Installs or replaces an APK; <paramref name="installer"/> sets the installer of record.</summary>
    public static async Task<string?> InstallAsync(string serial, string apk, string? installer = null) =>
        await RunAsync($"-s {serial} install -r {(installer != null ? $"-i {installer} " : "")}\"{apk}\"",
            TimeSpan.FromMinutes(2));

    private static async Task<string?> RunAsync(string args, TimeSpan? timeout = null)
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
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
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

/// <summary>
/// Plug and play over USB: for every phone with USB debugging, installs/updates the CamTel app,
/// grants the camera permission, launches it and forwards its port to the PC.
/// </summary>
public sealed class UsbPhones : IDisposable
{
    private const string Package = "io.github.ghermin.camtel";
    private const string ApkUrl = "https://github.com/Ghermin/camtel/releases/latest/download/CamTel.apk";
    private const int FirstLocalPort = 4757;

    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Phone> _ready = new();
    private readonly Dictionary<string, int> _ports = new();
    private readonly HashSet<string> _failed = new();

    public event Action? Changed;
    public event Action<string>? Status;

    public IReadOnlyList<Phone> Phones
    {
        get
        {
            lock (_ready) return _ready.Values.ToList();
        }
    }

    public void Start() => _ = Loop();

    private async Task Loop()
    {
        var adb = await Adb.EnsureAsync(s => Status?.Invoke(s), _cts.Token);
        while (adb == null && !_cts.IsCancellationRequested)
        {
            if (!await Delay(60_000)) return;
            adb = await Adb.EnsureAsync(s => Status?.Invoke(s), _cts.Token);
        }
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await PollAsync();
            }
            catch
            {
            }
            // Plugging a phone in wakes this loop right away (WM_DEVICECHANGE), so polling can be slow.
            try
            {
                await _wake.WaitAsync(10_000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>A USB device was plugged or unplugged: look for phones now.</summary>
    public void PollSoon()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>Removes our port forwards and stops the adb server if CamTel started it.</summary>
    public void Shutdown()
    {
        _cts.Cancel();
        if (Adb.StartedServer)
        {
            Adb.StopServerIfOwned();
            return;
        }
        foreach (var (serial, port) in _ports)
        {
            try
            {
                var adb = Adb.FindAdb();
                if (adb == null) break;
                using var p = Process.Start(new ProcessStartInfo(adb, $"-s {serial} forward --remove tcp:{port}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                p?.WaitForExit(2000);
            }
            catch
            {
            }
        }
    }

    private async Task PollAsync()
    {
        var devices = await Adb.DevicesAsync();
        var present = devices.Where(d => d.State == "device").Select(d => d.Serial).ToHashSet();

        bool removed;
        lock (_ready)
        {
            var gone = _ready.Keys.Where(k => !present.Contains(k)).ToList();
            foreach (var k in gone) _ready.Remove(k);
            removed = gone.Count > 0;
        }
        _failed.RemoveWhere(s => !present.Contains(s));
        if (removed) Changed?.Invoke();

        foreach (var d in devices)
        {
            if (d.State == "unauthorized")
                Status?.Invoke($"Sur le téléphone, accepte « Autoriser le débogage USB ? » (coche « Toujours autoriser »)");
            if (d.State != "device" || _failed.Contains(d.Serial)) continue;
            bool known;
            lock (_ready) known = _ready.ContainsKey(d.Serial);
            if (known) continue;

            var phone = await PrepareAsync(d);
            if (phone == null)
            {
                _failed.Add(d.Serial);
                continue;
            }
            lock (_ready) _ready[d.Serial] = phone;
            Changed?.Invoke();
        }
    }

    private async Task<Phone?> PrepareAsync(Adb.Device d)
    {
        var dump = await Adb.ShellAsync(d.Serial, $"dumpsys package {Package}") ?? "";
        var installed = InstalledVersion(dump);
        // An app that is its own installer of record can update itself over Wi-Fi without any prompt.
        var selfInstalled = dump.Contains($"installerPackageName={Package}");
        var outdated = installed == null || installed < Updater.Current;
        if (outdated || !selfInstalled)
        {
            Status?.Invoke(installed == null ? $"Installation de l'app CamTel sur {d.Model}…"
                : outdated ? $"Mise à jour de l'app CamTel sur {d.Model}…"
                : $"Préparation de CamTel sur {d.Model}…");
            var apk = await DownloadApkAsync();
            var ok = false;
            if (apk != null)
            {
                // "-i <self>" is refused while the package does not exist yet: plain install first.
                if (installed == null) await Adb.InstallAsync(d.Serial, apk);
                ok = (await Adb.InstallAsync(d.Serial, apk, installer: Package))?.Contains("Success") == true;
            }
            if (!ok && installed == null && InstalledVersion(await Adb.ShellAsync(d.Serial, $"dumpsys package {Package}") ?? "") == null)
            {
                Status?.Invoke($"Impossible d'installer l'app sur {d.Model}. Installe-la depuis {ApkUrl}");
                return null;
            }
        }

        await Adb.ShellAsync(d.Serial, $"pm grant {Package} android.permission.CAMERA");
        await Adb.ShellAsync(d.Serial, $"pm grant {Package} android.permission.POST_NOTIFICATIONS");
        await Adb.ShellAsync(d.Serial, $"appops set {Package} REQUEST_INSTALL_PACKAGES allow");

        // Launch the app unless it is already streaming.
        var services = await Adb.ShellAsync(d.Serial, $"dumpsys activity services {Package}/.CameraService") ?? "";
        if (!services.Contains("ServiceRecord"))
        {
            Status?.Invoke($"Démarrage de CamTel sur {d.Model}…");
            await Adb.ShellAsync(d.Serial, $"am start -n {Package}/.MainActivity");
        }

        if (!_ports.TryGetValue(d.Serial, out var port))
        {
            port = FirstLocalPort + _ports.Count;
            _ports[d.Serial] = port;
        }
        if (!await Adb.ForwardAsync(d.Serial, port, PhoneDiscovery.PhonePort)) return null;
        return new Phone(d.Model, "127.0.0.1", port, true) { Serial = d.Serial };
    }

    /// <summary>Starts the phone app again (e.g. after it was closed while the cable stays plugged).</summary>
    public static async Task LaunchAppAsync(Phone phone)
    {
        if (phone.Serial == null) return;
        await Adb.ShellAsync(phone.Serial, $"am start -n {Package}/.MainActivity");
    }

    /// <summary>Re-creates the port forward (adb forgets it when its server restarts).</summary>
    public static Task<bool> RefreshForwardAsync(Phone phone) =>
        phone.Serial == null ? Task.FromResult(true) : Adb.ForwardAsync(phone.Serial, phone.Port, PhoneDiscovery.PhonePort);

    private static Version? InstalledVersion(string dump)
    {
        var line = dump.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("versionName="));
        if (line == null) return null;
        return Version.TryParse(line["versionName=".Length..].Trim(), out var v) ? v : new Version(0, 0);
    }

    private async Task<string?> DownloadApkAsync()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "CamTel.apk");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            await using var src = await http.GetStreamAsync(ApkUrl, _cts.Token);
            await using var dst = File.Create(path);
            await src.CopyToAsync(dst, _cts.Token);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> Delay(int ms)
    {
        try
        {
            await Task.Delay(ms, _cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose() => _cts.Cancel();
}
