using System.Windows.Input;
using Gridline.Core.Addressing;

namespace Gridline.App.Controls;

/// <summary>Keyboard and mouse selection (§11.3): arrows move the active cell, Shift+arrows and mouse drag select a range.
/// Editing keys (typing, F2, Enter, Tab, Esc, Delete, §11.4) are added by the editing task.</summary>
public sealed partial class GridControl
{
    private bool _dragging;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Source is not { } src) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Left: src.MoveActiveCell(-1, 0, shift); break;
            case Key.Right: src.MoveActiveCell(1, 0, shift); break;
            case Key.Up: src.MoveActiveCell(0, -1, shift); break;
            case Key.Down: src.MoveActiveCell(0, 1, shift); break;
            case Key.PageUp: src.MoveActiveCell(0, -_fullRows, shift); break;
            case Key.PageDown: src.MoveActiveCell(0, _fullRows, shift); break;
            case Key.Home when ctrl: src.SelectCell(new CellAddress(1, 1), shift); break;
            case Key.Home: src.SelectCell(new CellAddress(1, src.ActiveCell.Row), shift); break;
            case Key.End when ctrl: src.SelectCell(src.GetLastUsedCell(), shift); break;
            // Plain End (Excel's End mode) is not implemented; it is swallowed so the ScrollViewer does not scroll the
            // view away from the active cell. Every key the ScrollViewer acts on (arrows, PageUp/PageDown, Home, End)
            // is therefore handled here.
            case Key.End: break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        e.Handled = true;
        var p = e.GetPosition(this);
        if (Source is not { } src || !GridMetrics.IsInCellArea(p)) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        src.SelectCell(GridMetrics.CellAtClamped(p, VisibleRange, _firstColumn, _firstRow), shift);
        _dragging = CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || Source is not { } src || e.LeftButton != MouseButtonState.Pressed) return;
        var cell = GridMetrics.CellAtClamped(e.GetPosition(this), VisibleRange, _firstColumn, _firstRow);
        if (cell != src.ActiveCell) src.SelectCell(cell, extend: true);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging) ReleaseMouseCapture();
        _dragging = false;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }
}
