using System.Globalization;
using System.Text.Json;

namespace LoadTester.Core.Extraction;

/// <summary>
/// Minimal JSON path: property names separated by dots, with optional [index] or ['name'] parts.
/// Examples: $.data.token, items[0].id, $[0]['display name'].
/// </summary>
public sealed class JsonPath
{
    private readonly Part[] _parts;

    private JsonPath(Part[] parts) => _parts = parts;

    public static bool TryParse(string? expression, out JsonPath path)
    {
        path = new JsonPath([]);
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var text = expression.Trim();
        if (text[0] == '$')
            text = text[1..];

        var parts = new List<Part>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                var close = text.IndexOf(']', i);
                if (close < 0)
                    return false;

                var inner = text[(i + 1)..close].Trim();
                if (inner.Length >= 2 && inner[0] is '\'' or '"' && inner[^1] == inner[0])
                    parts.Add(Part.Property(inner[1..^1]));
                else if (int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    parts.Add(Part.Item(index));
                else
                    return false;

                i = close + 1;
                continue;
            }

            // A property name, either after a dot or (without "$.") at the very start.
            if (text[i] == '.')
                i++;
            else if (i != 0)
                return false;

            var start = i;
            while (i < text.Length && text[i] != '.' && text[i] != '[')
                i++;
            if (i == start)
                return false;

            parts.Add(Part.Property(text[start..i]));
        }

        path = new JsonPath([.. parts]);
        return true;
    }

    /// <summary>Strings are returned unquoted; numbers, booleans, objects and arrays as raw JSON. Null counts as missing.</summary>
    public bool TryEvaluate(JsonElement root, out string value)
    {
        value = "";
        var current = root;
        foreach (var part in _parts)
        {
            if (part.Name is not null)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part.Name, out var next))
                    return false;
                current = next;
            }
            else
            {
                if (current.ValueKind != JsonValueKind.Array || part.Index >= current.GetArrayLength())
                    return false;
                current = current[part.Index];
            }
        }

        switch (current.ValueKind)
        {
            case JsonValueKind.String:
                value = current.GetString() ?? "";
                return true;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return false;
            default:
                value = current.GetRawText();
                return true;
        }
    }

    private readonly record struct Part(string? Name, int Index)
    {
        public static Part Property(string name) => new(name, -1);
        public static Part Item(int index) => new(null, index);
    }
}
