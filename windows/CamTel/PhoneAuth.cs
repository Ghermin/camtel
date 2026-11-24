using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CamTel;

/// <summary>
/// Pairing secret of a phone (see Pairing.kt in the Android app). The token never goes on the
/// network after pairing: each request carries HMAC-SHA256(token, "camtel-auth:" + host), bound to
/// the address we connect to, so a device that pretends to be the phone gets nothing reusable.
/// </summary>
public static class PhoneAuth
{
    /// <summary>Value of the Authorization header for <paramref name="host"/> (an IP address).</summary>
    public static string? Header(string? tokenHex, string host)
    {
        if (tokenHex == null) return null;
        byte[] key;
        try
        {
            key = Convert.FromHexString(tokenHex);
        }
        catch (FormatException)
        {
            return null;
        }
        var name = host.Trim().Trim('[', ']').ToLowerInvariant();
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("camtel-auth:" + name));
        return "CamTel " + Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>What a phone says about itself without authentication (401 body) or in /status.</summary>
    public sealed record Identity(string? Id, string Name, bool PairingOpen);

    public static Identity? ParseIdentity(string? json)
    {
        if (json == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("app", out var app) || app.GetString() != "CamTel") return null;
            var id = r.TryGetProperty("id", out var i) ? i.GetString() : null;
            var name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var open = r.TryGetProperty("pairing", out var p) && p.ValueKind == JsonValueKind.True;
            return new Identity(id, name, open);
        }
        catch
        {
            return null;
        }
    }

    public enum PairResult { Paired, WrongCode, Closed, Unreachable }

    /// <summary>Sends the code shown on the phone; on success returns its id and token.</summary>
    public static async Task<(PairResult Result, string? Id, string? Token)> PairAsync(Uri api, string code, CancellationToken ct)
    {
        var (status, body) = await MjpegClient.RequestAsync(new Uri(api, "pair?code=" + Uri.EscapeDataString(code)),
            null, ct, TimeSpan.FromSeconds(10));
        if (status == HttpStatusCode.OK && body != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var id = doc.RootElement.GetProperty("id").GetString();
                var token = doc.RootElement.GetProperty("token").GetString();
                if (id != null && token != null) return (PairResult.Paired, id, token);
            }
            catch
            {
            }
            return (PairResult.Unreachable, null, null);
        }
        if (status == HttpStatusCode.Forbidden && body?.Contains("closed") == true) return (PairResult.Closed, null, null);
        if (status == HttpStatusCode.Forbidden) return (PairResult.WrongCode, null, null);
        return (PairResult.Unreachable, null, null);
    }
}
