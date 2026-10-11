using System.Globalization;
using System.Text;

namespace LoadTester.Core.Templates;

/// <summary>Values a template can reference while it is rendered.</summary>
public interface ITemplateContext
{
    int VirtualUserId { get; }
    long Iteration { get; }
    bool TryGetVariable(string name, out string value);
}

public sealed class TemplateException(string message) : Exception(message);

/// <summary>
/// Text with {{variable}} and {{$function}} placeholders. Parsed once per run and rendered for every request.
/// </summary>
public sealed class Template
{
    private readonly Segment[] _segments;
    private readonly string? _constant;

    private Template(Segment[] segments)
    {
        _segments = segments;
        if (segments.All(s => s.Kind == SegmentKind.Literal))
            _constant = string.Concat(segments.Select(s => s.Text));
    }

    public static Template Empty { get; } = new([]);

    /// <summary>True when the text has no placeholders.</summary>
    public bool IsConstant => _constant is not null;

    public IEnumerable<string> VariableNames =>
        _segments.Where(s => s.Kind == SegmentKind.Variable).Select(s => s.Text!).Distinct(StringComparer.Ordinal);

    public static Template Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Empty;

        var segments = new List<Segment>();
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf("{{", position, StringComparison.Ordinal);
            if (open < 0)
            {
                segments.Add(Segment.Literal(text[position..]));
                break;
            }

            if (open > position)
                segments.Add(Segment.Literal(text[position..open]));

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
                throw new TemplateException("'{{' ifadesinin kapanışı ('}}') eksik.");

            segments.Add(ParseExpression(text[(open + 2)..close].Trim()));
            position = close + 2;
        }

        return new Template([.. segments]);
    }

    public static bool TryParse(string? text, out Template template, out string? error)
    {
        try
        {
            template = Parse(text);
            error = null;
            return true;
        }
        catch (TemplateException ex)
        {
            template = Empty;
            error = ex.Message;
            return false;
        }
    }

    public string Render(ITemplateContext context)
    {
        if (_constant is not null)
            return _constant;

        var builder = new StringBuilder();
        foreach (var segment in _segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Literal:
                    builder.Append(segment.Text);
                    break;
                case SegmentKind.Variable:
                    if (context.TryGetVariable(segment.Text!, out var value))
                        builder.Append(value);
                    break;
                case SegmentKind.Guid:
                    builder.Append(Guid.NewGuid().ToString("D"));
                    break;
                case SegmentKind.Timestamp:
                    builder.Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
                    break;
                case SegmentKind.RandomInt:
                    builder.Append(Random.Shared.NextInt64(segment.Min, segment.Max + 1).ToString(CultureInfo.InvariantCulture));
                    break;
                case SegmentKind.VirtualUser:
                    builder.Append(context.VirtualUserId.ToString(CultureInfo.InvariantCulture));
                    break;
                case SegmentKind.Iteration:
                    builder.Append(context.Iteration.ToString(CultureInfo.InvariantCulture));
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Letters, digits and underscores; must not start with a digit.</summary>
    public static bool IsValidVariableName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
            return false;

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    private static Segment ParseExpression(string expression)
    {
        if (expression.Length == 0)
            throw new TemplateException("Boş yer tutucu: {{ }}");

        if (expression[0] != '$')
        {
            if (!IsValidVariableName(expression))
                throw new TemplateException($"Geçersiz değişken adı: {expression}");
            return Segment.Variable(expression);
        }

        var name = expression[1..];
        var arguments = Array.Empty<string>();
        var paren = name.IndexOf('(');
        if (paren >= 0)
        {
            if (!name.EndsWith(')'))
                throw new TemplateException($"Kapanmamış parantez: {expression}");

            arguments = name[(paren + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
            if (arguments is [""])
                arguments = [];
            name = name[..paren].Trim();
        }

        var kind = name.ToLowerInvariant() switch
        {
            "guid" => SegmentKind.Guid,
            "timestamp" => SegmentKind.Timestamp,
            "randomint" => SegmentKind.RandomInt,
            "vu" => SegmentKind.VirtualUser,
            "iteration" => SegmentKind.Iteration,
            _ => throw new TemplateException($"Bilinmeyen fonksiyon: ${name}"),
        };

        if (kind != SegmentKind.RandomInt)
        {
            if (arguments.Length > 0)
                throw new TemplateException($"${name} parametre almaz.");
            return new Segment(kind);
        }

        if (arguments.Length == 0)
            return new Segment(kind, Min: 0, Max: int.MaxValue);

        if (arguments.Length == 2
            && int.TryParse(arguments[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var min)
            && int.TryParse(arguments[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var max)
            && min <= max)
        {
            return new Segment(kind, Min: min, Max: max);
        }

        throw new TemplateException($"$randomInt iki tam sayı alır (en az, en çok): {expression}");
    }

    private enum SegmentKind
    {
        Literal,
        Variable,
        Guid,
        Timestamp,
        RandomInt,
        VirtualUser,
        Iteration,
    }

    private readonly record struct Segment(SegmentKind Kind, string? Text = null, long Min = 0, long Max = 0)
    {
        public static Segment Literal(string text) => new(SegmentKind.Literal, text);
        public static Segment Variable(string name) => new(SegmentKind.Variable, name);
    }
}
