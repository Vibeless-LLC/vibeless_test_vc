using System.Globalization;
using System.Text;

namespace Gridline.Core;

/// <summary>
/// An immutable cell value: blank, number, text, boolean or error (§9.1).
/// Reading a payload of the wrong kind throws <see cref="InvalidOperationException"/>.
/// </summary>
public readonly struct CellValue : IEquatable<CellValue>
{
    private readonly double _number;
    private readonly string? _text; // text payload or error code
    private readonly bool _boolean;

    private CellValue(ValueKind kind, double number = 0, string? text = null, bool boolean = false)
    {
        Kind = kind;
        _number = number;
        _text = text;
        _boolean = boolean;
    }

    public ValueKind Kind { get; }

    /// <summary>The blank value; also <c>default(CellValue)</c>.</summary>
    public static CellValue Blank => default;

    public double Number => Kind == ValueKind.Number ? _number : throw Wrong(ValueKind.Number);
    public string Text => Kind == ValueKind.Text ? _text! : throw Wrong(ValueKind.Text);
    public bool Boolean => Kind == ValueKind.Boolean ? _boolean : throw Wrong(ValueKind.Boolean);
    public string Error => Kind == ValueKind.Error ? _text! : throw Wrong(ValueKind.Error);

    private InvalidOperationException Wrong(ValueKind wanted) =>
        new($"Value is {Kind}, not {wanted}.");

    /// <summary>Negative zero becomes 0; NaN and infinities become the #NUM! error.</summary>
    public static CellValue FromNumber(double value)
    {
        if (!double.IsFinite(value)) return FromError(ErrorCodes.Num);
        if (value == 0) value = 0.0; // normalises -0
        return new CellValue(ValueKind.Number, number: value);
    }

    public static CellValue FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new CellValue(ValueKind.Text, text: text);
    }

    public static CellValue FromBoolean(bool value) => new(ValueKind.Boolean, boolean: value);

    public static CellValue FromError(string code)
    {
        if (!ErrorCodes.IsKnown(code))
            throw new ArgumentException($"Unknown error code '{code}'.", nameof(code));
        return new CellValue(ValueKind.Error, text: code);
    }

    /// <summary>Canonical string form used by the CLI and tests (§8.3).</summary>
    public string ToCanonicalString() => Kind switch
    {
        ValueKind.Number => "N:" + _number.ToString("R", CultureInfo.InvariantCulture),
        ValueKind.Text => "T:" + Escape(_text!),
        ValueKind.Boolean => _boolean ? "B:TRUE" : "B:FALSE",
        ValueKind.Error => "E:" + _text,
        _ => "BLANK",
    };

    /// <summary>Escapes backslash, tab, LF and CR as two-character sequences (§8.3), as used by the T: canonical form.</summary>
    public static string EscapeText(string s) => Escape(s);

    private static string Escape(string s)
    {
        const char Bs = (char)92;
        if (s.AsSpan().IndexOfAny(Bs, (char)9, (char)10) < 0 && s.IndexOf((char)13) < 0) return s;
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case Bs: sb.Append(Bs).Append(Bs); break;
                case (char)9: sb.Append(Bs).Append('t'); break;
                case (char)10: sb.Append(Bs).Append('n'); break;
                case (char)13: sb.Append(Bs).Append('r'); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    public bool Equals(CellValue other) =>
        Kind == other.Kind && Kind switch
        {
            ValueKind.Number => _number == other._number,
            ValueKind.Text or ValueKind.Error => string.Equals(_text, other._text, StringComparison.Ordinal),
            ValueKind.Boolean => _boolean == other._boolean,
            _ => true,
        };

    public override bool Equals(object? obj) => obj is CellValue o && Equals(o);

    public override int GetHashCode() => Kind switch
    {
        ValueKind.Number => HashCode.Combine(Kind, _number),
        ValueKind.Text or ValueKind.Error => HashCode.Combine(Kind, _text),
        ValueKind.Boolean => HashCode.Combine(Kind, _boolean),
        _ => HashCode.Combine(Kind),
    };

    public static bool operator ==(CellValue left, CellValue right) => left.Equals(right);
    public static bool operator !=(CellValue left, CellValue right) => !left.Equals(right);

    public override string ToString() => ToCanonicalString();
}
