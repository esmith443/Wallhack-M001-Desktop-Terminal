using System.Globalization;
using System.IO;

namespace WallhackTerminal.Services;

public enum BatteryTrend { Unknown, Charging, Discharging, Full, Steady, Settling }

public sealed record BatteryStats(
    int Percent,
    BatteryTrend Trend,
    double? RatePerHour,
    TimeSpan? Estimate,
    DateTime? LastFull,
    int Low24h,
    int High24h);

public sealed class BatteryMonitor
{
    public sealed record Sample(DateTime Time, int? Mouse, int? Dock);

    static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(10);
    static readonly TimeSpan Settle = TimeSpan.FromMinutes(10);
    readonly List<Sample> _samples = [];
    int _appendsSincePrune;

    static string FilePath => Path.Combine(AppSettings.Directory, "battery.csv");

    public BatteryMonitor()
    {
        Load();
    }

    void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var cutoff = DateTime.Now - Keep;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var parts = line.Split(',');
                if (parts.Length < 3 || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)) continue;
                if (time < cutoff) continue;
                _samples.Add(new Sample(time, ParseLevel(parts[1]), ParseLevel(parts[2])));
            }
        }
        catch (Exception ex)
        {
            Log.Error("battery history load failed", ex);
        }
    }

    static int? ParseLevel(string text) => int.TryParse(text, out int v) && v is > 0 and <= 100 ? v : null;

    public void Record(int? mouse, int? dock)
    {
        var now = DateTime.Now;
        if (_samples.Count > 0)
        {
            var last = _samples[^1];
            if (last.Mouse == mouse && last.Dock == dock && now - last.Time < Heartbeat) return;
        }
        _samples.Add(new Sample(now, mouse, dock));
        Persist(_samples[^1]);
    }

    void Persist(Sample sample)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.Directory);
            if (++_appendsSincePrune >= 200)
            {
                _appendsSincePrune = 0;
                var cutoff = DateTime.Now - Keep;
                _samples.RemoveAll(s => s.Time < cutoff);
                File.WriteAllLines(FilePath, _samples.Select(Format));
                return;
            }
            File.AppendAllText(FilePath, Format(sample) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Log.Error("battery history save failed", ex);
        }
    }

    static string Format(Sample s) =>
        $"{s.Time.ToString("o", CultureInfo.InvariantCulture)},{s.Mouse?.ToString() ?? ""},{s.Dock?.ToString() ?? ""}";

    public IEnumerable<(DateTime Time, int? Level)> Series(bool mouse) =>
        _samples.Select(s => (s.Time, mouse ? s.Mouse : s.Dock));

    public BatteryStats? Stats(bool mouse)
    {
        var all = Series(mouse).ToList();
        if (all.Count == 0 || all[^1].Level is not int current) return null;
        var now = DateTime.Now;

        var known = all.Where(p => p.Level is not null).Select(p => (p.Time, Level: p.Level!.Value)).ToList();
        var lastFull = known.LastOrDefault(p => p.Level >= 100).Time;
        var day = known.Where(p => now - p.Time <= TimeSpan.FromHours(24)).Select(p => p.Level).DefaultIfEmpty(current).ToList();
        DateTime? full = lastFull == default ? null : lastFull;

        int gap = all.FindLastIndex(p => p.Level is null);
        var inserted = gap >= 0 ? all[gap + 1].Time : (DateTime?)null;
        if (inserted is DateTime at && now - at < Settle)
            return new BatteryStats(current, BatteryTrend.Settling, null, null, full, day.Min(), day.Max());

        var points = all.Skip(gap + 1)
            .Where(p => inserted is not DateTime at2 || p.Time - at2 >= Settle)
            .Select(p => (p.Time, Level: p.Level!.Value))
            .ToList();
        if (points.Count == 0) points.Add((all[^1].Time, current));

        int changeIndex = points.FindLastIndex(p => p.Level != current);
        int direction = changeIndex < 0 ? 0 : Math.Sign(current - points[changeIndex].Level);
        DateTime? changedAt = changeIndex < 0 ? null : points[changeIndex + 1].Time;

        var trend = current >= 100 ? BatteryTrend.Full
            : direction > 0 && now - changedAt < TimeSpan.FromMinutes(90) ? BatteryTrend.Charging
            : direction < 0 && now - changedAt < TimeSpan.FromHours(6) ? BatteryTrend.Discharging
            : now - points[0].Time > TimeSpan.FromMinutes(30) ? BatteryTrend.Steady
            : BatteryTrend.Unknown;

        double? rate = null;
        TimeSpan? estimate = null;
        if (trend is BatteryTrend.Charging or BatteryTrend.Discharging)
        {
            int start = points.Count - 1;
            while (start > 0 && Math.Sign(points[start].Level - points[start - 1].Level) is var step && (step == direction || step == 0)
                   && now - points[start - 1].Time < TimeSpan.FromHours(24))
                start--;
            while (start < points.Count - 1 && points[start + 1].Level == points[start].Level) start++;

            int delta = Math.Abs(current - points[start].Level);
            double hours = (points[^1].Time - points[start].Time).TotalHours;
            if (delta >= 2 && hours >= 0.25)
            {
                rate = delta / hours;
                double remaining = trend == BatteryTrend.Discharging ? current : 100 - current;
                estimate = TimeSpan.FromHours(remaining / rate.Value);
            }
        }

        return new BatteryStats(current, trend, rate, estimate, full, day.Min(), day.Max());
    }
}
