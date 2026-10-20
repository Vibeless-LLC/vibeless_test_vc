using Gridline.Core.Addressing;

namespace Gridline.Core.Dependencies;

/// <summary>Packs a <see cref="CellAddress"/> into one int (column in bits 17+, row in bits 0-16) for cheap hashing.</summary>
internal static class CellKey
{
    private const int RowBits = 17; // GridBounds.MaxRows = 100,000 < 2^17
    private const int RowMask = (1 << RowBits) - 1;

    public static int Pack(CellAddress a) => (a.Column << RowBits) | a.Row;
    public static int Pack(int column, int row) => (column << RowBits) | row;
    public static int Column(int key) => key >> RowBits;
    public static int Row(int key) => key & RowMask;
    public static CellAddress Unpack(int key) => new(key >> RowBits, key & RowMask);
}

/// <summary>
/// A cell key qualified by a workbook's stable internal sheet id (CR1.1, D-52): the sheet id in the high 32 bits and the
/// <see cref="CellKey"/> in the low 32. Sheet ids are stable across rename and move, so a sheet never re-keys its cells.
/// Sheet 0 is the sheet of every single-sheet API (the first sheet of a workbook, and the sheet of
/// <see cref="DependencyGraph"/>'s <see cref="CellAddress"/> members).
/// </summary>
internal static class SheetCellKey
{
    public static long Pack(int sheet, int cellKey) => ((long)sheet << 32) | (uint)cellKey;
    public static long Pack(int sheet, CellAddress a) => Pack(sheet, CellKey.Pack(a));
    public static int Sheet(long key) => (int)(key >> 32);
    public static int Cell(long key) => (int)(key & 0xFFFF_FFFFL);
    public static CellAddress Address(long key) => CellKey.Unpack(Cell(key));
}
