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
