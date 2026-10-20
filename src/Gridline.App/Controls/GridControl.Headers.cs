using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Gridline.App.Controls;

/// <summary>Row and column headers (§11.3, §11.5, R75): a left-click selects the whole row or column (Shift+click and
/// dragging extend it, in GridControl.Input.cs); a right-click selects the header unless it is inside the current
/// whole-row or whole-column selection, then the grid's <see cref="FrameworkElement.ContextMenu"/> (Insert, Delete) opens.
/// The context menu opens only on headers. See DECISIONS D-49.</summary>
public sealed partial class GridControl
{
    private bool _menuOnHeader;

    private IGridHeaderSource? HeaderSource => Source as IGridHeaderSource;

    /// <summary>The header under <paramref name="p"/>: a row header left of the cells, a column header above them; null
    /// for the cell area and the corner box.</summary>
    internal (HeaderAxis Axis, int Index)? HeaderAt(Point p)
    {
        bool rowHeader = p.X < GridMetrics.RowHeaderWidth && p.Y >= GridMetrics.ColumnHeaderHeight;
        bool columnHeader = p.Y < GridMetrics.ColumnHeaderHeight && p.X >= GridMetrics.RowHeaderWidth;
        if (!rowHeader && !columnHeader) return null;
        var cell = GridMetrics.CellAtClamped(p, VisibleRange, _firstColumn, _firstRow);
        return rowHeader ? (HeaderAxis.Rows, cell.Row) : (HeaderAxis.Columns, cell.Column);
    }

    /// <summary>The active cell's other coordinate for a header selection: the first visible column (rows) or row
    /// (columns), so selecting a header never scrolls the view.</summary>
    private int ActiveOther(HeaderAxis axis) => axis == HeaderAxis.Rows ? _firstColumn : _firstRow;

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        _menuOnHeader = false;
        if (_editor.IsAncestorOf(e.OriginalSource as DependencyObject ?? this)) return;
        Focus(); // commits an open edit (OnGotKeyboardFocus)
        if (HeaderSource is not { } headers || HeaderAt(e.GetPosition(this)) is not { } header) return;
        // Only when the selection now contains the clicked header: if Focus() could not commit the open edit, the
        // selection stays put and the menu must not act on a stale one.
        _menuOnHeader = headers.PrepareHeaderMenu(header.Axis, header.Index, ActiveOther(header.Axis));
    }

    /// <summary>The menu belongs to the headers: a right-click on a cell or the corner opens nothing. Opened from the
    /// keyboard (Shift+F10, the menu key) it needs a whole-row or whole-column selection.</summary>
    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        bool fromKeyboard = e.CursorLeft < 0 && e.CursorTop < 0;
        bool allowed = HeaderSource is { HasHeaderSelection: true } && (fromKeyboard || _menuOnHeader);
        _menuOnHeader = false;
        if (!allowed)
        {
            e.Handled = true;
            return;
        }
        base.OnContextMenuOpening(e);
    }
}
