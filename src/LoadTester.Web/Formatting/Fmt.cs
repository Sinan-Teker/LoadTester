using System.Globalization;
using LoadTester.Core.Scenarios;
using LoadTester.Web.Data;

namespace LoadTester.Web.Formatting;

/// <summary>Turkish display formatting for numbers, durations and dates.</summary>
public static class Fmt
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Count(long value) => value.ToString("N0", Tr);

    /// <summary>Per-second rates: fewer decimals as the number grows.</summary>
    public static string Rate(double value) =>
        value.ToString(value >= 100 ? "N0" : value >= 10 ? "N1" : "N2", Tr);

    public static string Ms(double? value) => value switch
    {
        null => "—",
        >= 10_000 => (value.Value / 1000).ToString("N1", Tr) + " sn",
        >= 100 => value.Value.ToString("N0", Tr) + " ms",
        >= 10 => value.Value.ToString("N1", Tr) + " ms",
        _ => value.Value.ToString("N2", Tr) + " ms",
    };

    public static string Percent(double ratio)
    {
        if (ratio <= 0)
            return "%0";

        var percent = ratio * 100;
        return percent < 0.01 ? "<%0,01" : "%" + percent.ToString(percent >= 10 ? "0.#" : "0.##", Tr);
    }

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(Tr) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("N1", Tr) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("N1", Tr) + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("N2", Tr) + " GB",
    };

    /// <summary>Axis labels: 950, 1,5K, 2M.</summary>
    public static string Compact(double value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.#", Tr) + "M",
        >= 1_000 => (value / 1_000).ToString("0.#", Tr) + "K",
        _ => value.ToString("0.##", Tr),
    };

    /// <summary>"45 sn", "2 dk 30 sn", "1 sa 5 dk".</summary>
    public static string Duration(double seconds)
    {
        var total = (long)Math.Round(Math.Max(0, seconds));
        if (total < 60)
            return $"{total} sn";

        long hours = total / 3600, minutes = total % 3600 / 60, rest = total % 60;
        if (hours > 0)
            return minutes > 0 ? $"{hours} sa {minutes} dk" : $"{hours} sa";
        return rest > 0 ? $"{minutes} dk {rest} sn" : $"{minutes} dk";
    }

    /// <summary>Elapsed time as "01:05" or "1:02:03".</summary>
    public static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, seconds)));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    /// <summary>Stored UTC time shown in the server's local time zone.</summary>
    public static string Timestamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm", Tr);

    public static string LoadSummary(LoadProfile load) =>
        $"{Count(load.PeakTarget)} {LoadUnit(load.Mode)} · {Duration(load.TotalSeconds)}";

    public static string LoadUnit(LoadMode mode) => mode == LoadMode.VirtualUsers ? "kullanıcı" : "iterasyon/sn";

    public static string HttpVersion(HttpProtocolVersion version) => version switch
    {
        HttpProtocolVersion.Http2 => "HTTP/2",
        HttpProtocolVersion.Http3 => "HTTP/3",
        _ => version.ToString(),
    };

    public static string StatusLabel(RunStatus status) => status switch
    {
        RunStatus.Running => "Çalışıyor",
        RunStatus.Completed => "Tamamlandı",
        RunStatus.Stopped => "Durduruldu",
        RunStatus.Failed => "Hata",
        RunStatus.Interrupted => "Yarıda kaldı",
        _ => status.ToString(),
    };
}
