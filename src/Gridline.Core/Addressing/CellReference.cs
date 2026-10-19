using System.Globalization;

namespace Gridline.Core.Addressing;

/// <summary>An address as written in a formula (§4.2), keeping the $ absolute markers.</summary>
public readonly record struct CellReference(CellAddress Address, bool ColumnAbsolute, bool RowAbsolute)
{
    /// <summary>Parses [$]COL[$]ROW. Case-insensitive; rejects whitespace, A0, AAA1, rows over 100000, and leading-zero rows such as A01 (one canonical spelling per row).</summary>
    public static bool TryParse(string? text, out CellReference reference)
        => TryParse(text.AsSpan(), out reference);

    internal static bool TryParse(ReadOnlySpan<char> s, out CellReference reference)
    {
        reference = default;
        int i = 0;
        bool colAbs = false, rowAbs = false;
        if (i < s.Length && s[i] == '$') { colAbs = true; i++; }
        int letterStart = i;
        while (i < s.Length && IsAsciiLetter(s[i])) i++;
        if (!ColumnName.TryParse(s[letterStart..i], out int col)) return false;
        if (i < s.Length && s[i] == '$') { rowAbs = true; i++; }
        var digits = s[i..];
        if (digits.Length is < 1 or > 6 || digits[0] == '0') return false;
        int row = 0;
        foreach (char ch in digits)
        {
            if (ch < '0' || ch > '9') return false;
            row = row * 10 + (ch - '0');
        }
        if (row < 1 || row > GridBounds.MaxRows) return false;
        reference = new CellReference(new CellAddress(col, row), colAbs, rowAbs);
        return true;
    }

    /// <exception cref="ArgumentException">The text is not a valid address.</exception>
    public static CellReference Parse(string text)
        => TryParse(text, out var r) ? r : throw new ArgumentException($"Invalid cell address: '{text}'.", nameof(text));

    /// <summary>Formula form with $ markers, e.g. "$B$7".</summary>
    public override string ToString()
        => (ColumnAbsolute ? "$" : "") + ColumnName.FromNumber(Address.Column)
         + (RowAbsolute ? "$" : "") + Address.Row.ToString(CultureInfo.InvariantCulture);

    private static bool IsAsciiLetter(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a' && c < 128;
}
