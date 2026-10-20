using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Gridline.Core.Addressing;

namespace Gridline.App.Controls;

/// <summary>Logical scrolling (§11.3): offsets and extents are in whole columns and rows, 0-based.</summary>
public sealed partial class GridControl : IScrollInfo
{
    private const int WheelRows = 3;
    private int _firstColumn = 1;
    private int _firstRow = 1;
    private int _fullColumns = 1;
    private int _fullRows = 1;

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => GridBounds.MaxColumns;
    public double ExtentHeight => GridBounds.MaxRows;
    public double ViewportWidth => _fullColumns;
    public double ViewportHeight => _fullRows;
    public double HorizontalOffset => _firstColumn - 1;
    public double VerticalOffset => _firstRow - 1;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(VerticalOffset - 1);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 1);
    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - 1);
    public void LineRight() => SetHorizontalOffset(HorizontalOffset + 1);
    public void PageUp() => SetVerticalOffset(VerticalOffset - _fullRows);
    public void PageDown() => SetVerticalOffset(VerticalOffset + _fullRows);
    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - _fullColumns);
    public void PageRight() => SetHorizontalOffset(HorizontalOffset + _fullColumns);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - WheelRows);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + WheelRows);
    public void MouseWheelLeft() => SetHorizontalOffset(HorizontalOffset - WheelRows);
    public void MouseWheelRight() => SetHorizontalOffset(HorizontalOffset + WheelRows);

    public void SetHorizontalOffset(double offset)
    {
        int first = ClampFirst(offset, GridBounds.MaxColumns, _fullColumns);
        if (first == _firstColumn) return;
        _firstColumn = first;
        OnScrolled();
    }

    public void SetVerticalOffset(double offset)
    {
        int first = ClampFirst(offset, GridBounds.MaxRows, _fullRows);
        if (first == _firstRow) return;
        _firstRow = first;
        OnScrolled();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    /// <summary>A 0-based offset to a 1-based first index, so that the last full page ends at <paramref name="max"/>.</summary>
    private static int ClampFirst(double offset, int max, int fullCount)
    {
        if (double.IsNaN(offset)) offset = 0;
        double clamped = Math.Clamp(Math.Floor(offset), 0, Math.Max(0, max - fullCount));
        return (int)clamped + 1;
    }

    private void OnScrolled()
    {
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    private void UpdateViewport(Size size)
    {
        int cols = GridMetrics.FullColumns(size.Width), rows = GridMetrics.FullRows(size.Height);
        bool changed = cols != _fullColumns || rows != _fullRows;
        _fullColumns = cols;
        _fullRows = rows;
        // Re-clamp in case the viewport grew at the end of the grid.
        _firstColumn = ClampFirst(_firstColumn - 1, GridBounds.MaxColumns, _fullColumns);
        _firstRow = ClampFirst(_firstRow - 1, GridBounds.MaxRows, _fullRows);
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    /// <summary>Scrolls the minimum amount so that <paramref name="cell"/> is fully visible.</summary>
    internal void ScrollIntoView(CellAddress cell)
    {
        if (cell.Column < _firstColumn) SetHorizontalOffset(cell.Column - 1);
        else if (cell.Column > _firstColumn + _fullColumns - 1) SetHorizontalOffset(cell.Column - _fullColumns);
        if (cell.Row < _firstRow) SetVerticalOffset(cell.Row - 1);
        else if (cell.Row > _firstRow + _fullRows - 1) SetVerticalOffset(cell.Row - _fullRows);
    }
}
