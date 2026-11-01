using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace CamTel;

public sealed record Phone(string Name, string Host, int Port, bool Usb)
{
    /// <summary>adb serial for USB phones (Host/Port are then the local forwarded endpoint).</summary>
    public string? Serial { get; init; }

    public override string ToString() => Usb ? $"USB · {Name}" : $"Wi-Fi · {Name} ({Host})";
}

/// <summary>
/// Finds phones running CamTel on the local networks by probing port 4747 on every address of
/// each local subnet. Outbound connections only, so Windows never shows a firewall prompt.
/// </summary>
public sealed class PhoneDiscovery : IDisposable
{
    public const int PhonePort = 4747;
    private const int MaxHostsPerInterface = 1024;

    private readonly Dictionary<string, (Phone Phone, DateTime Seen)> _phones = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    public event Action? Changed;

    /// <summary>When false (e.g. while streaming), scans run less often.</summary>
    public volatile bool Eager = true;

    public IReadOnlyList<Phone> Phones
    {
        get
        {
            lock (_phones)
                return _phones.Values.Select(v => v.Phone).OrderBy(p => p.Name).ToList();
        }
    }

    public void Start() => _ = Loop();

    /// <summary>Requests an immediate scan (e.g. when the user opens the phone list).</summary>
    public void ScanNow() => _wake.Release();

    private readonly SemaphoreSlim _wake = new(0);

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
            }
            try
            {
                await _wake.WaitAsync(Eager ? 3000 : 20000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        var targets = CandidateAddresses().ToList();
        var found = new System.Collections.Concurrent.ConcurrentBag<Phone>();
        using var gate = new SemaphoreSlim(192);
        var tasks = targets.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await IsPortOpenAsync(ip, ct))
                {
                    var phone = await IdentifyAsync(ip, ct);
                    if (phone != null) found.Add(phone);
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);

        var changed = false;
        lock (_phones)
        {
            foreach (var p in found)
            {
                changed |= !_phones.TryGetValue(p.Host, out var old) || old.Phone != p;
                _phones[p.Host] = (p, DateTime.UtcNow);
            }
            // Keep a phone for one missed scan before dropping it.
            var stale = _phones.Where(kv => DateTime.UtcNow - kv.Value.Seen > TimeSpan.FromSeconds(Eager ? 10 : 50))
                .Select(kv => kv.Key).ToList();
            foreach (var k in stale) _phones.Remove(k);
            changed |= stale.Count > 0;
        }
        if (changed) Changed?.Invoke();
    }

    private static async Task<bool> IsPortOpenAsync(IPAddress ip, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(600);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, PhonePort), timeout.Token);
            return true;
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    private async Task<Phone?> IdentifyAsync(IPAddress ip, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"http://{ip}:{PhonePort}/status", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("app").GetString() != "CamTel") return null;
            var name = doc.RootElement.GetProperty("name").GetString() ?? ip.ToString();
            return new Phone(name, ip.ToString(), PhonePort, false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Every host address of each active IPv4 LAN (capped to the /22 around our own address).</summary>
    private static IEnumerable<IPAddress> CandidateAddresses()
    {
        var seen = new HashSet<uint>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var self = ToUint(ua.Address);
                if ((self >> 16) == 0xA9FE) continue; // 169.254.x.x link-local
                // Large networks are capped to the 1024 addresses around our own one.
                var prefix = Math.Clamp(ua.PrefixLength, 32 - (int)Math.Log2(MaxHostsPerInterface), 30);
                var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
                var network = self & mask;
                var broadcast = network | ~mask;
                for (var a = network + 1; a < broadcast; a++)
                {
                    if (a == self || !seen.Add(a)) continue;
                    yield return FromUint(a);
                }
            }
        }
    }

    private static uint ToUint(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IPAddress FromUint(uint a) =>
        new(new[] { (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a });

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
