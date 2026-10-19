namespace Gridline.Core.Addressing;

/// <summary>Column letter / number conversion (§4.2). A=1 .. Z=26, AA=27 .. ZZ=702.</summary>
public static class ColumnName
{
    /// <summary>Parses 1 or 2 ASCII letters (case-insensitive) into a column number 1..702.</summary>
    public static bool TryParse(string? letters, out int column)
        => TryParse(letters.AsSpan(), out column);

    internal static bool TryParse(ReadOnlySpan<char> letters, out int column)
    {
        column = 0;
        if (letters.Length is < 1 or > 2) return false;
        int n = 0;
        foreach (char ch in letters)
        {
            char u = (char)(ch & ~0x20);
            if (ch > 127 || u < 'A' || u > 'Z') return false;
            n = n * 26 + (u - 'A' + 1);
        }
        column = n;
        return true;
    }

    /// <summary>Converts a column number 1..702 to uppercase letters.</summary>
    public static string FromNumber(int column)
    {
        if (column < 1 || column > GridBounds.MaxColumns)
            throw new ArgumentOutOfRangeException(nameof(column), column, "Column must be 1..702.");
        return column <= 26
            ? ((char)('A' + column - 1)).ToString()
            : string.Create(2, column, static (span, c) =>
            {
                span[0] = (char)('A' + (c - 1) / 26 - 1);
                span[1] = (char)('A' + (c - 1) % 26);
            });
    }
}
