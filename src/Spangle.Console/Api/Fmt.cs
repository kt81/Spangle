using System.Globalization;

namespace Spangle.Console.Api;

/// <summary>Display formatting shared by the pages; always invariant.</summary>
public static class Fmt
{
    public static string Bitrate(long bps) => bps switch
    {
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bps / 1_000_000.0:F1} Mbps"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{bps / 1_000.0:F0} kbps"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bps} bps"),
    };

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):F1} GiB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):F1} MiB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 10):F1} KiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
    };

    public static string Uptime(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:D2}");
    }

    public static string Time(DateTimeOffset ts) =>
        ts.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public static string LevelName(int level) => level switch
    {
        0 => "TRC", 1 => "DBG", 2 => "INF", 3 => "WRN", 4 => "ERR", 5 => "CRT", _ => "???",
    };

    public static string LevelClass(int level) => level switch
    {
        <= 1 => "lv-trace", 2 => "lv-info", 3 => "lv-warn", _ => "lv-error",
    };

    /// <summary>Polyline points for a fixed-height sparkline; newest sample last.</summary>
    public static string SparklinePoints(IReadOnlyList<long> samples, int width, int height)
    {
        if (samples.Count < 2)
        {
            return "";
        }
        long max = 1;
        foreach (long s in samples)
        {
            max = Math.Max(max, s);
        }
        var sb = new System.Text.StringBuilder(samples.Count * 8);
        for (var i = 0; i < samples.Count; i++)
        {
            double x = (double)i / (samples.Count - 1) * width;
            double y = height - 1 - samples[i] / (double)max * (height - 2);
            sb.Append(CultureInfo.InvariantCulture, $"{x:F1},{y:F1} ");
        }
        return sb.ToString();
    }
}
