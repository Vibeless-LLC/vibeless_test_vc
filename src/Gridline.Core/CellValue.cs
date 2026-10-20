using System.Globalization;
using System.Text;

namespace Gridline.Core;

/// <summary>
/// An immutable cell value: blank, number, text, boolean or error (§9.1).
/// Each payload accessor returns its default on the wrong kind (§9.1): <see cref="Number"/> 0, <see cref="Text"/> "",
/// <see cref="Boolean"/> false, <see cref="Error"/> "".
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

    /// <summary>The number; 0 unless <see cref="Kind"/> is <see cref="ValueKind.Number"/> (§9.1).</summary>
    public double Number => Kind == ValueKind.Number ? _number : 0;

    /// <summary>The text; "" unless <see cref="Kind"/> is <see cref="ValueKind.Text"/> (§9.1).</summary>
    public string Text => Kind == ValueKind.Text ? _text! : "";

    /// <summary>The boolean; false unless <see cref="Kind"/> is <see cref="ValueKind.Boolean"/> (§9.1).</summary>
    public bool Boolean => Kind == ValueKind.Boolean && _boolean;

    /// <summary>The error code; "" unless <see cref="Kind"/> is <see cref="ValueKind.Error"/> (§9.1).</summary>
    public string Error => Kind == ValueKind.Error ? _text! : "";

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

    private static readonly System.Buffers.SearchValues<char> NeedsEscape =
        System.Buffers.SearchValues.Create(['\\', '\t', '\n', '\r']);

    private static string Escape(string s)
    {
        int first = s.AsSpan().IndexOfAny(NeedsEscape);
        if (first < 0) return s;
        var sb = new StringBuilder(s.Length + 8);
        sb.Append(s, 0, first);
        for (int i = first; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '\\': sb.Append('\\').Append('\\'); break;
                case '\t': sb.Append('\\').Append('t'); break;
                case '\n': sb.Append('\\').Append('n'); break;
                case '\r': sb.Append('\\').Append('r'); break;
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
