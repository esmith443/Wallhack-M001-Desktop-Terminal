using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using WallhackTerminal.Protocol;

namespace WallhackTerminal.Services;

public enum FirmwareStatus { Unknown, UpToDate, Available, Newer }

public sealed partial class FirmwareService
{
    public const string WebTerminal = "https://terminal.wallhack.com/";
    static readonly HttpClient Http = AppInfo.CreateHttpClient(TimeSpan.FromSeconds(45));

    [GeneratedRegex("<script[^>]+src=\"([^\"]+\\.js)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();

    [GeneratedRegex("\\{date:\"(?<date>[^\"]*)\",version:\"(?<version>\\d+\\.\\d+\\.\\d+)\",mouseNordic:(?<mouse>\\d+),receiverNordic:(?<receiver>\\d+),receiverNxp:(?<nxp>\\d+)(?:,changelog:\"(?<log>(?:[^\"\\\\]|\\\\.)*)\")?")]
    private static partial Regex ReleaseEntry();

    public FirmwareRelease? Latest { get; private set; } = Newest(M001.Releases);
    public DateTime? LastChecked { get; private set; }
    public string? Error { get; private set; }
    public bool Checking { get; private set; }

    public event Action? Changed;

    public async Task CheckAsync()
    {
        if (Checking) return;
        Checking = true;
        Changed?.Invoke();
        try
        {
            string html = await Http.GetStringAsync(WebTerminal);
            var found = new List<FirmwareRelease>();
            foreach (Match script in ScriptTag().Matches(html))
            {
                var uri = new Uri(new Uri(WebTerminal), script.Groups[1].Value);
                found.AddRange(Parse(await Http.GetStringAsync(uri)));
                if (found.Count > 0) break;
            }
            if (found.Count == 0) throw new InvalidDataException("the web terminal's firmware list was not found");

            M001.KnownReleases = found
                .Concat(M001.Releases.Where(r => found.All(f => f.Version != r.Version)))
                .OrderByDescending(r => Version.Parse(r.Version))
                .ToList();
            Latest = Newest(found);
            Error = null;
        }
        catch (Exception ex)
        {
            Log.Error("firmware check failed", ex);
            Error = AppInfo.Friendly(ex);
        }
        finally
        {
            LastChecked = DateTime.Now;
            Checking = false;
            Changed?.Invoke();
        }
    }

    static IEnumerable<FirmwareRelease> Parse(string script)
    {
        foreach (Match m in ReleaseEntry().Matches(script))
        {
            string? changelog = m.Groups["log"].Success ? Unescape(m.Groups["log"].Value) : null;
            yield return new FirmwareRelease(m.Groups["version"].Value, m.Groups["date"].Value,
                int.Parse(m.Groups["mouse"].Value), int.Parse(m.Groups["receiver"].Value), int.Parse(m.Groups["nxp"].Value), changelog);
        }
    }

    static string Unescape(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<string>($"\"{raw}\"") ?? raw;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    static FirmwareRelease? Newest(IEnumerable<FirmwareRelease> releases) => releases.MaxBy(r => Version.Parse(r.Version));

    public FirmwareStatus StatusFor(FirmwareVersions? device)
    {
        if (device is null || Latest is not { } latest) return FirmwareStatus.Unknown;
        if (device.Release is { } installed)
        {
            int compare = Version.Parse(installed.Version).CompareTo(Version.Parse(latest.Version));
            return compare < 0 ? FirmwareStatus.Available : compare > 0 ? FirmwareStatus.Newer : FirmwareStatus.UpToDate;
        }
        bool behind = device.Mouse < latest.Mouse || device.Receiver < latest.Receiver || device.ReceiverNxp < latest.ReceiverNxp;
        return behind ? FirmwareStatus.Available : FirmwareStatus.Newer;
    }
}
