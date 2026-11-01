using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CamTel;

public sealed record Phone(string Name, string Host, int Port, bool Usb)
{
    public override string ToString() => Usb ? $"USB · {Name}" : $"Wi-Fi · {Name} ({Host})";
}

/// <summary>Listens for the UDP beacons ("CAMTEL1|name|port") the phone app broadcasts.</summary>
public sealed class PhoneDiscovery : IDisposable
{
    public const int Port = 4748;

    private readonly Dictionary<string, (Phone Phone, DateTime Seen)> _phones = new();
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    public event Action? Changed;

    public IReadOnlyList<Phone> Phones
    {
        get
        {
            lock (_phones)
                return _phones.Values.Select(v => v.Phone).OrderBy(p => p.Name).ToList();
        }
    }

    public void Start()
    {
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        }
        catch (SocketException)
        {
            _udp = null;
            return;
        }
        _ = ReceiveLoop();
        _ = PruneLoop();
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested && _udp != null)
        {
            try
            {
                var r = await _udp.ReceiveAsync(_cts.Token);
                var parts = Encoding.UTF8.GetString(r.Buffer).Split('|');
                if (parts.Length < 3 || parts[0] != "CAMTEL1" || !int.TryParse(parts[2], out var port)) continue;
                var host = r.RemoteEndPoint.Address.ToString();
                bool added;
                lock (_phones)
                {
                    added = !_phones.ContainsKey(host);
                    _phones[host] = (new Phone(parts[1], host, port, false), DateTime.UtcNow);
                }
                if (added) Changed?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
            }
        }
    }

    private async Task PruneLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            bool removed;
            lock (_phones)
            {
                var stale = _phones.Where(kv => DateTime.UtcNow - kv.Value.Seen > TimeSpan.FromSeconds(5))
                    .Select(kv => kv.Key).ToList();
                foreach (var k in stale) _phones.Remove(k);
                removed = stale.Count > 0;
            }
            if (removed) Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
    }
}
