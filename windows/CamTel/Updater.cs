using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CamTel;

/// <summary>Checks GitHub for a newer release and swaps the installed exe for it.</summary>
public static class Updater
{
    private const string LatestApi = "https://api.github.com/repos/Ghermin/camtel/releases/latest";

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public sealed record Release(Version Version, string ExeUrl, long ExeSize);

    /// <summary>The latest release if it is newer than this exe, otherwise null.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = NewClient();
            var json = await http.GetStringAsync(LatestApi, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v'), out var latest)) return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            if (latest <= Current) return null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != "CamTel.exe") continue;
                return new Release(latest, asset.GetProperty("browser_download_url").GetString()!,
                    asset.GetProperty("size").GetInt64());
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>Downloads the new exe; returns its path, or null if the download is incomplete.</summary>
    public static async Task<string?> DownloadAsync(Release release, CancellationToken ct = default)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "CamTel-update");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"CamTel-{release.Version}.exe");
            if (File.Exists(path) && new FileInfo(path).Length == release.ExeSize) return path;
            var partial = path + ".part";
            using (var http = NewClient())
            await using (var src = await http.GetStreamAsync(release.ExeUrl, ct))
            await using (var dst = File.Create(partial))
            {
                await src.CopyToAsync(dst, ct);
            }
            if (new FileInfo(partial).Length != release.ExeSize)
            {
                File.Delete(partial);
                return null;
            }
            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Starts the downloaded exe. It installs itself over this one: it signals this process to
    /// quit, copies itself to the install folder and relaunches from there.
    /// </summary>
    public static void Apply(string exe, bool hidden)
    {
        var args = hidden ? "--minimized --updated" : "--updated";
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
    }

    /// <summary>Removes downloaded installers once the update is running.</summary>
    public static void CleanUp()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "CamTel-update");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
        }
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CamTel", Current.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
