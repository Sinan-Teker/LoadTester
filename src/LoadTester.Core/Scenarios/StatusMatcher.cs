using System.Globalization;

namespace LoadTester.Core.Scenarios;

/// <summary>Parsed expected-status expression such as "200", "200-299, 304" or "2xx".</summary>
public sealed class StatusMatcher
{
    private readonly (int Min, int Max)[] _ranges;

    private StatusMatcher((int Min, int Max)[] ranges) => _ranges = ranges;

    /// <summary>Accepts every status code.</summary>
    public static StatusMatcher Any { get; } = new([]);

    public bool Matches(int statusCode)
    {
        if (_ranges.Length == 0)
            return true;

        foreach (var (min, max) in _ranges)
        {
            if (statusCode >= min && statusCode <= max)
                return true;
        }

        return false;
    }

    public static bool TryParse(string? text, out StatusMatcher matcher)
    {
        matcher = Any;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var ranges = new List<(int, int)>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length == 3 && part[0] is >= '1' and <= '5' && part[1..].Equals("xx", StringComparison.OrdinalIgnoreCase))
            {
                var hundred = (part[0] - '0') * 100;
                ranges.Add((hundred, hundred + 99));
                continue;
            }

            var dash = part.IndexOf('-');
            var minText = dash < 0 ? part : part[..dash].Trim();
            var maxText = dash < 0 ? part : part[(dash + 1)..].Trim();
            if (!TryParseCode(minText, out var min) || !TryParseCode(maxText, out var max) || min > max)
                return false;

            ranges.Add((min, max));
        }

        matcher = new StatusMatcher([.. ranges]);
        return true;
    }

    private static bool TryParseCode(string text, out int code) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out code) && code is >= 100 and <= 599;
}
