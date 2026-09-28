namespace WallhackTerminal.Protocol;

public sealed record CurvePoint(int Speed, double Gain);

public sealed record Curve(IReadOnlyList<CurvePoint> Points)
{
    public double GainAt(double speed)
    {
        var p = Points;
        if (speed <= p[0].Speed) return p[0].Gain;
        for (int i = 1; i < p.Count; i++)
        {
            if (speed <= p[i].Speed)
            {
                double t = (speed - p[i - 1].Speed) / Math.Max(1, p[i].Speed - p[i - 1].Speed);
                return p[i - 1].Gain + t * (p[i].Gain - p[i - 1].Gain);
            }
        }
        return p[^1].Gain;
    }
}

public static class Curves
{
    public const int PointCount = 5;
    public const int MaxSpeed = 280;
    public const double MinGain = 0.10, MaxGain = 6.00;

    public static readonly string[] ModeNames = ["CLASSIC", "NATURAL", "JUMP", "CUSTOM"];
    public const int CustomMode = 3;
    static readonly int[] Addresses = [M001.Addr.CurveClassic, M001.Addr.CurveNatural, M001.Addr.CurveJump, M001.Addr.CurveCustom];

    public static int Address(int mode) => Addresses[mode];
    public static int Length(int mode) => mode == CustomMode ? PointCount * 4 : PointCount * 2;

    public static Curve Make(params (int Speed, double Gain)[] points) => new(points.Select(p => new CurvePoint(p.Speed, p.Gain)).ToList());

    public static readonly Curve[] Defaults =
    [
        Make((0, 1), (20, 1.1), (40, 1.2), (70, 1.35), (100, 1.5)),
        Make((0, 1), (11, 1.45), (20, 1.5), (35, 1.5), (100, 1.5)),
        Make((0, 1), (9, 1), (21, 1.5), (24, 1.5), (100, 1.5)),
        Make((0, 1), (70, 1), (140, 1), (210, 1), (280, 1)),
    ];

    public static Curve? Parse(int mode, byte[] d)
    {
        if (d.Length < Length(mode)) return null;
        var points = new List<CurvePoint>();
        for (int i = 0; i < PointCount; i++)
        {
            points.Add(mode == CustomMode
                ? new CurvePoint(d[i * 4] | (d[i * 4 + 1] << 8), (d[i * 4 + 2] | (d[i * 4 + 3] << 8)) / 100.0)
                : new CurvePoint(d[i * 2], d[i * 2 + 1] / 100.0));
        }
        var curve = new Curve(points);
        return Validate(curve) is null ? curve : null;
    }

    public static string? Validate(Curve c)
    {
        if (c.Points.Count != PointCount) return $"a curve needs exactly {PointCount} points";
        int last = -1;
        for (int i = 0; i < c.Points.Count; i++)
        {
            var p = c.Points[i];
            if (p.Speed < 0 || p.Speed > MaxSpeed) return $"point {i + 1} speed must be 0..{MaxSpeed}";
            if (p.Speed <= last) return "speeds must be strictly increasing";
            last = p.Speed;
            int g = (int)Math.Round(p.Gain * 100);
            if (g < MinGain * 100 || g > MaxGain * 100) return $"point {i + 1} gain must be {MinGain:0.00}..{MaxGain:0.00}";
        }
        return null;
    }

    public static byte[] EncodeCustom(Curve c)
    {
        if (Validate(c) is string error) throw new InvalidOperationException("Invalid curve: " + error);
        var data = new byte[PointCount * 4];
        for (int i = 0; i < PointCount; i++)
        {
            int speed = c.Points[i].Speed, gain = (int)Math.Round(c.Points[i].Gain * 100);
            data[i * 4] = (byte)speed;
            data[i * 4 + 1] = (byte)(speed >> 8);
            data[i * 4 + 2] = (byte)gain;
            data[i * 4 + 3] = (byte)(gain >> 8);
        }
        return M001.WriteArea(M001.Addr.CurveCustom, data);
    }
}
