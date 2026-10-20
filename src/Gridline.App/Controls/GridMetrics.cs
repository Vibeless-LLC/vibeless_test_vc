using System.Windows;
using Gridline.Core.Addressing;

namespace Gridline.App.Controls;

/// <summary>The cells a viewport shows: 1-based, inclusive. Partially visible trailing cells are included.</summary>
internal readonly record struct VisibleCells(int FirstColumn, int LastColumn, int FirstRow, int LastRow)
{
    public int ColumnCount => LastColumn - FirstColumn + 1;
    public int RowCount => LastRow - FirstRow + 1;
    public int CellCount => ColumnCount * RowCount;
}

/// <summary>
/// Fixed grid geometry (§11.3). Every column has the same width and every row the same height, so mapping between
/// pixels and cells is O(1) at any scroll position, including column ZZ and row 100,000.
/// </summary>
internal static class GridMetrics
{
    public const double ColumnWidth = 80;
    public const double RowHeight = 22;
    public const double RowHeaderWidth = 60;
    public const double ColumnHeaderHeight = 22;
    public const double TextPadding = 3;

    /// <summary>Columns that fit completely in <paramref name="width"/> (at least 1).</summary>
    public static int FullColumns(double width) => FullCount(width - RowHeaderWidth, ColumnWidth, GridBounds.MaxColumns);

    /// <summary>Rows that fit completely in <paramref name="height"/> (at least 1).</summary>
    public static int FullRows(double height) => FullCount(height - ColumnHeaderHeight, RowHeight, GridBounds.MaxRows);

    private static int FullCount(double space, double cell, int max)
    {
        if (!(space > 0)) return 1; // also NaN
        double n = Math.Floor(space / cell);
        return (int)Math.Clamp(n, 1, max);
    }

    /// <summary>The cells drawn for a viewport of <paramref name="size"/> whose top-left cell is
    /// (<paramref name="firstColumn"/>, <paramref name="firstRow"/>), including partially visible ones, clamped to the grid.</summary>
    public static VisibleCells Visible(int firstColumn, int firstRow, Size size)
    {
        int cols = PartialCount(size.Width - RowHeaderWidth, ColumnWidth);
        int rows = PartialCount(size.Height - ColumnHeaderHeight, RowHeight);
        int lastColumn = Math.Min(GridBounds.MaxColumns, firstColumn + cols - 1);
        int lastRow = Math.Min(GridBounds.MaxRows, firstRow + rows - 1);
        return new VisibleCells(firstColumn, lastColumn, firstRow, lastRow);
    }

    private static int PartialCount(double space, double cell)
    {
        if (!(space > 0)) return 1;
        double n = Math.Ceiling(space / cell);
        return (int)Math.Clamp(n, 1, 1_000_000);
    }

    /// <summary>The left edge of <paramref name="column"/> in control coordinates.</summary>
    public static double ColumnLeft(int column, int firstColumn) => RowHeaderWidth + (column - firstColumn) * ColumnWidth;

    /// <summary>The top edge of <paramref name="row"/> in control coordinates.</summary>
    public static double RowTop(int row, int firstRow) => ColumnHeaderHeight + (row - firstRow) * RowHeight;

    /// <summary>The cell under <paramref name="point"/>, after clamping the point into the cell area and the visible cells
    /// (so a drag outside the control selects the edge cell).</summary>
    public static CellAddress CellAtClamped(Point point, VisibleCells visible, int firstColumn, int firstRow)
    {
        int column = firstColumn + (int)Math.Floor((point.X - RowHeaderWidth) / ColumnWidth);
        int row = firstRow + (int)Math.Floor((point.Y - ColumnHeaderHeight) / RowHeight);
        if (double.IsNaN(point.X)) column = visible.FirstColumn;
        if (double.IsNaN(point.Y)) row = visible.FirstRow;
        column = Math.Clamp(column, visible.FirstColumn, visible.LastColumn);
        row = Math.Clamp(row, visible.FirstRow, visible.LastRow);
        return new CellAddress(column, row);
    }

    /// <summary>True when <paramref name="point"/> is inside the cell area (not on a header).</summary>
    public static bool IsInCellArea(Point point) => point.X >= RowHeaderWidth && point.Y >= ColumnHeaderHeight;
}
