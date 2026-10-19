namespace Gridline.Core.Addressing;

/// <summary>A cell position (§4.2): 1-based column and row within the grid bounds (§4.3).</summary>
public readonly record struct CellAddress
{
    public int Column { get; }
    public int Row { get; }

    public CellAddress(int column, int row)
    {
        if (column < 1 || column > GridBounds.MaxColumns)
            throw new ArgumentOutOfRangeException(nameof(column), column, "Column must be 1..702.");
        if (row < 1 || row > GridBounds.MaxRows)
            throw new ArgumentOutOfRangeException(nameof(row), row, "Row must be 1..100000.");
        Column = column;
        Row = row;
    }

    /// <summary>Parses [$]COL[$]ROW, ignoring the $ markers. Case-insensitive; no whitespace allowed.</summary>
    public static bool TryParse(string? text, out CellAddress address)
    {
        if (CellReference.TryParse(text, out var r)) { address = r.Address; return true; }
        address = default;
        return false;
    }

    /// <exception cref="ArgumentException">The text is not a valid address.</exception>
    public static CellAddress Parse(string text)
        => TryParse(text, out var a) ? a : throw new ArgumentException($"Invalid cell address: '{text}'.", nameof(text));

    /// <summary>Canonical form: uppercase letters, no $, e.g. "A1".</summary>
    public override string ToString() => ColumnName.FromNumber(Column) + Row.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
