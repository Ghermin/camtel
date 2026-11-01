using System.Net.Http.Headers;
using System.Text;

namespace CamTel;

/// <summary>Reads a multipart/x-mixed-replace MJPEG stream and reports each JPEG frame.</summary>
public static class MjpegClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(4),
        UseProxy = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Streams frames until the connection drops, stalls for 5 s, or <paramref name="ct"/> is cancelled.</summary>
    public static async Task StreamAsync(Uri uri, Action<byte[]> onFrame, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(TimeSpan.FromSeconds(8));
        using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(stall.Token);
        var reader = new PartReader(stream);

        while (!ct.IsCancellationRequested)
        {
            stall.CancelAfter(TimeSpan.FromSeconds(5));
            var frame = await reader.ReadPartAsync(stall.Token);
            if (frame == null) return;
            onFrame(frame);
        }
    }

    /// <summary>GET on the phone's HTTP API, returns the body or null on failure.</summary>
    public static async Task<string?> GetStringAsync(Uri uri, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            return await Http.GetStringAsync(uri, cts.Token);
        }
        catch
        {
            return null;
        }
    }

    private sealed class PartReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buf = new byte[256 * 1024];
        private int _start;
        private int _end;

        public PartReader(Stream stream) => _stream = stream;

        public async Task<byte[]?> ReadPartAsync(CancellationToken ct)
        {
            // Skip to the boundary line, then read part headers.
            int contentLength = -1;
            string? line;
            do
            {
                line = await ReadLineAsync(ct);
                if (line == null) return null;
            } while (!line.StartsWith("--", StringComparison.Ordinal));

            while ((line = await ReadLineAsync(ct)) != null && line.Length > 0)
            {
                var colon = line.IndexOf(':');
                if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line.AsSpan(colon + 1).Trim(), out contentLength);
            }
            if (line == null) return null;

            if (contentLength > 0)
            {
                var data = new byte[contentLength];
                var copied = 0;
                while (copied < contentLength)
                {
                    if (_start == _end && !await FillAsync(ct)) return null;
                    var n = Math.Min(contentLength - copied, _end - _start);
                    Buffer.BlockCopy(_buf, _start, data, copied, n);
                    _start += n;
                    copied += n;
                }
                return data;
            }

            // No Content-Length: read until the JPEG end marker (FF D9).
            using var ms = new MemoryStream();
            var prev = -1;
            while (true)
            {
                if (_start == _end && !await FillAsync(ct)) return null;
                var b = _buf[_start++];
                ms.WriteByte(b);
                if (prev == 0xFF && b == 0xD9) return ms.ToArray();
                prev = b;
            }
        }

        private async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (_start == _end && !await FillAsync(ct)) return null;
                var b = _buf[_start++];
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)b);
                if (sb.Length > 8192) throw new InvalidDataException("ligne trop longue");
            }
        }

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            _start = 0;
            _end = await _stream.ReadAsync(_buf.AsMemory(), ct);
            return _end > 0;
        }
    }
}
