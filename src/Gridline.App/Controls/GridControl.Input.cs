using System.Windows.Input;
using Gridline.Core.Addressing;

namespace Gridline.App.Controls;

/// <summary>Keyboard and mouse (§11.3, §11.4): arrows move the active cell, Shift+arrows and mouse drag select a range;
/// typing starts an edit, F2 edits the input, Enter/Tab move, Delete clears the selection. While the in-cell editor has
/// focus the keys belong to it (Enter/Tab/Esc are handled in GridControl.Editing.cs).</summary>
public sealed partial class GridControl
{
    private bool _dragging;
    private HeaderAxis? _dragAxis; // a drag that started on a row or column header (R75)

    /// <summary>Test seam (final review, §11.3, §11.4): where the grid reads the Shift/Ctrl/Alt state. Defaults to the
    /// global <see cref="Keyboard.Modifiers"/>; in-process control tests set it explicitly, so a physically held or stuck
    /// key on the desktop cannot change their result.</summary>
    internal Func<ModifierKeys> ModifierProvider { get; set; } = static () => Keyboard.Modifiers;

    private ModifierKeys Modifiers => ModifierProvider();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Source is not { } src) return;
        if (_editor.IsKeyboardFocusWithin)
        {
            // The editor keeps navigation keys it does not use (Up/Down/PageUp/PageDown in a one-line box) so they neither
            // move the active cell nor scroll the ScrollViewer (D-38).
            if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Left or Key.Right or Key.Home or Key.End)
                e.Handled = true;
            return;
        }
        ModifierKeys modifiers = Modifiers;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
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
            // §11.4 editing keys. Without an open edit, Enter and Tab just move (Shift reverses), as in Excel.
            case Key.F2 when EditSource is { } edit: BeginEditFromGrid(edit, null); break;
            case Key.Enter when EditSource is { } edit: edit.CommitEdit(0, shift ? -1 : 1); break;
            case Key.Tab when EditSource is { } edit: edit.CommitEdit(shift ? -1 : 1, 0); break;
            case Key.Delete when EditSource is { } edit && !ctrl: edit.ClearSelection(); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>A printable character typed on the grid starts an edit that replaces the input (§11.4). Text arrives through
    /// the TextInput route (TextCompositionManager), so IME results and non-ASCII characters work. Ctrl+key and Alt+key
    /// produce ControlText/SystemText, not Text, so they never get here with text; a chord with exactly one of Ctrl and
    /// Alt is rejected as well (Ctrl+Alt together is AltGr, which types characters on many layouts).</summary>
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || _editor.IsKeyboardFocusWithin || EditSource is not { } edit) return;
        if (!IsPrintable(e.Text)) return;
        var mods = Modifiers & (ModifierKeys.Control | ModifierKeys.Alt);
        if (mods is ModifierKeys.Control or ModifierKeys.Alt) return;
        BeginEditFromGrid(edit, e.Text);
        e.Handled = true;
    }

    internal static bool IsPrintable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text)
            if (char.IsControl(c)) return false;
        return true;
    }

    /// <summary>Keyboard focus coming to the grid itself (a click on a cell or header, Enter in the formula bar) commits an
    /// open edit to its cell first, as in Excel (D-38). Focus moving to the formula bar or a menu keeps the edit open.</summary>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (ReferenceEquals(e.NewFocus, this) && EditSource is { IsEditing: true } edit) edit.CommitEdit(0, 0);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled || _editor.IsAncestorOf(e.OriginalSource as System.Windows.DependencyObject ?? this)) return;
        Focus(); // commits an open edit (OnGotKeyboardFocus)
        e.Handled = true;
        var p = e.GetPosition(this);
        if (Source is not { } src) return;
        bool shift = (Modifiers & ModifierKeys.Shift) != 0;
        if (HeaderSource is { } headers && HeaderAt(p) is { } header)
        {
            // R75: a row or column header selects the whole row or column; Shift+click and dragging extend it.
            headers.SelectHeaders(header.Axis, header.Index, ActiveOther(header.Axis), shift);
            _dragAxis = header.Axis;
            _dragging = CaptureMouse();
            return;
        }
        if (!GridMetrics.IsInCellArea(p)) return;
        src.SelectCell(GridMetrics.CellAtClamped(p, VisibleRange, _firstColumn, _firstRow), shift);
        _dragAxis = null;
        _dragging = CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || Source is not { } src || e.LeftButton != MouseButtonState.Pressed) return;
        var cell = GridMetrics.CellAtClamped(e.GetPosition(this), VisibleRange, _firstColumn, _firstRow);
        if (_dragAxis is { } axis && HeaderSource is { } headers)
        {
            int index = axis == HeaderAxis.Rows ? cell.Row : cell.Column;
            int current = axis == HeaderAxis.Rows ? src.ActiveCell.Row : src.ActiveCell.Column;
            if (index != current) headers.SelectHeaders(axis, index, ActiveOther(axis), extend: true);
            return;
        }
        if (cell != src.ActiveCell) src.SelectCell(cell, extend: true);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging) ReleaseMouseCapture();
        _dragging = false;
        _dragAxis = null;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
        _dragAxis = null;
    }
}
