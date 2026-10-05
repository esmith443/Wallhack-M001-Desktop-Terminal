using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace WallhackTerminal.Services;

public static class AppInfo
{
    public const string Owner = "esmith443";
    public const string Repo = "Wallhack-M001-Desktop-Terminal";
    public const string ProductName = "WH_TERMINAL Desktop";

    public static Version Version { get; } = Normalize(typeof(AppInfo).Assembly.GetName().Version);
    public static string VersionText => Version.ToString(3);
    public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases";

    public static Version Normalize(Version? v) => v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));

    public static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WallhackTerminal", VersionText));
        return client;
    }

    public static string Friendly(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "GitHub rate limit reached — try again later",
        HttpRequestException => "could not reach the server — check your internet connection",
        TaskCanceledException => "the request timed out",
        UnauthorizedAccessException => "no permission to replace the app in its folder — download it from the release page instead",
        IOException io => io.Message,
        InvalidDataException bad => bad.Message,
        _ => ex.Message,
    };
}

public enum UpdatePhase { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

public sealed record AppRelease(Version Version, string Tag, string Notes, string PageUrl, DateTime? Published, string? AssetUrl, long AssetSize, string? Sha256);

public sealed class UpdateService
{
    const string AssetName = "WallhackTerminal.exe";
    static readonly HttpClient Http = AppInfo.CreateHttpClient(TimeSpan.FromMinutes(5));

    public AppRelease? Latest { get; private set; }
    public UpdatePhase Phase { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public DateTime? LastChecked { get; private set; }
    public bool Busy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;
    public bool Available => Latest is { AssetUrl: not null } latest && latest.Version > AppInfo.Version;

    public event Action? Changed;

    void Set(UpdatePhase phase, string? error = null)
    {
        Phase = phase;
        Error = error;
        Changed?.Invoke();
    }

    public async Task CheckAsync()
    {
        if (Busy) return;
        Set(UpdatePhase.Checking);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{AppInfo.Owner}/{AppInfo.Repo}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request);
            LastChecked = DateTime.Now;
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Latest = null;
                Set(UpdatePhase.UpToDate);
                return;
            }
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Latest = ParseRelease(document.RootElement);
            Set(Available ? UpdatePhase.Available : UpdatePhase.UpToDate);
        }
        catch (Exception ex)
        {
            LastChecked = DateTime.Now;
            Log.Error("app update check failed", ex);
            Set(UpdatePhase.Failed, AppInfo.Friendly(ex));
        }
    }

    static AppRelease ParseRelease(JsonElement root)
    {
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version)) throw new InvalidDataException($"unexpected release tag \"{tag}\"");

        string? url = null, sha = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            url = asset.GetProperty("browser_download_url").GetString();
            size = asset.GetProperty("size").GetInt64();
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = d[7..];
        }

        DateTime? published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && p.TryGetDateTime(out var at)
            ? at.ToLocalTime()
            : null;
        string notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? AppInfo.ReleasesPage : AppInfo.ReleasesPage;
        return new AppRelease(version, tag, notes, page, published, url, size, sha);
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        string text = tag.Trim().TrimStart('v', 'V');
        int suffix = text.IndexOfAny(['-', '+', ' ']);
        if (suffix >= 0) text = text[..suffix];
        if (Version.TryParse(text, out var parsed))
        {
            version = AppInfo.Normalize(parsed);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    public async Task<bool> DownloadAndInstallAsync()
    {
        if (Busy || !Available || Latest is not { AssetUrl: { } url } release) return false;
        string? target = Environment.ProcessPath;
        if (target is null) return false;
        string staging = Path.Combine(Path.GetTempPath(), "WallhackTerminal-update");
        string file = Path.Combine(staging, $"WallhackTerminal-{release.Version.ToString(3)}.exe");
        try
        {
            Directory.CreateDirectory(staging);
            Progress = 0;
            Set(UpdatePhase.Downloading);
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? release.AssetSize;
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var destination = File.Create(file);
                var buffer = new byte[128 * 1024];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    if (total <= 0) continue;
                    double progress = Math.Min(100, Math.Floor(received * 100.0 / total));
                    if (progress == Progress) continue;
                    Progress = progress;
                    Changed?.Invoke();
                }
            }
            Verify(file, release);
            Set(UpdatePhase.Installing);
            Install(target, file);
            TryDelete(staging);
            Log.Info($"installed V{release.Version.ToString(3)} over V{AppInfo.VersionText}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("app update failed", ex);
            Set(UpdatePhase.Failed, AppInfo.Friendly(ex));
            return false;
        }
    }

    static void Verify(string file, AppRelease release)
    {
        var info = new FileInfo(file);
        if (release.AssetSize > 0 && info.Length != release.AssetSize) throw new InvalidDataException("the download is incomplete");
        if (release.Sha256 is { } expected)
        {
            using var stream = File.OpenRead(file);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the download failed its checksum");
        }
        if (!string.Equals(FileVersionInfo.GetVersionInfo(file).ProductName, AppInfo.ProductName, StringComparison.Ordinal))
            throw new InvalidDataException("the downloaded file is not WH_TERMINAL Desktop");
    }

    static void Install(string target, string downloaded)
    {
        string backup = BackupPath(target);
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(target, backup);
        try
        {
            File.Copy(downloaded, target);
        }
        catch
        {
            if (File.Exists(target)) File.Delete(target);
            File.Move(backup, target);
            throw;
        }
    }

    static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch (Exception ex)
        {
            Log.Error("update cleanup failed", ex);
        }
    }

    static string BackupPath(string target) => Path.ChangeExtension(target, ".old.exe");

    public static void CleanUpPreviousVersion()
    {
        if (Environment.ProcessPath is not { } target) return;
        string backup = BackupPath(target);
        if (!File.Exists(backup)) return;
        Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    File.Delete(backup);
                    return;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                await Task.Delay(500);
            }
        });
    }
}
