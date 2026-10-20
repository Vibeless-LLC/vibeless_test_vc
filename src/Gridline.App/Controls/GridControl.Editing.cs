using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Gridline.Core;

namespace Gridline.App.Controls;

/// <summary>
/// The in-cell editor (§11.4, D-38): a TextBox hosted as the grid's only visual child and arranged over the active cell,
/// so it follows the cell when the grid scrolls (clipped to the cell area). Its text mirrors
/// <see cref="IGridEditSource.EditText"/>, which the formula bar shows too. It is shown whenever the source has an open
/// edit, and focused when the edit was started from the grid (typing, F2).
/// </summary>
public sealed partial class GridControl
{
    private readonly TextBox _editor = CreateEditor();
    private bool _syncingEditor;

    /// <summary>The in-cell editor (test and view hook: the window points R49 Undo/Redo at it).</summary>
    internal TextBox Editor => _editor;

    private IGridEditSource? EditSource => Source as IGridEditSource;

    private static TextBox CreateEditor()
    {
        var editor = new TextBox
        {
            Visibility = Visibility.Collapsed,
            MaxLength = Workbook.MaxInputLength, // §11.4: the editor caps input like the formula bar
            BorderThickness = new Thickness(0),
            Padding = new Thickness(GridMetrics.TextPadding - 2, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            AcceptsReturn = false,
            AcceptsTab = false,
        };
        AutomationProperties.SetAutomationId(editor, "CellEditor");
        AutomationProperties.SetName(editor, "Cell Editor");
        return editor;
    }

    private void InitializeEditor()
    {
        AddVisualChild(_editor);
        AddLogicalChild(_editor);
        _editor.TextChanged += OnEditorTextChanged;
        _editor.PreviewKeyDown += OnEditorPreviewKeyDown;
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _editor : throw new ArgumentOutOfRangeException(nameof(index));

    protected override System.Collections.IEnumerator LogicalChildren => new[] { _editor }.GetEnumerator();

    /// <summary>Opens an edit from the grid (typed text, or null for F2) and moves keyboard focus into the editor with the
    /// caret at the end. OnEditStateChanged has already scrolled the cell into view and emptied the editor's undo
    /// history (R49).</summary>
    private void BeginEditFromGrid(IGridEditSource edit, string? replacement)
    {
        edit.BeginEdit(replacement);
        if (!edit.IsEditing) return;
        SyncEditor(); // normally already done by OnEditStateChanged, which also cleared the history
        _editor.UpdateLayout();
        _editor.Focus();
        _editor.CaretIndex = _editor.Text.Length;
    }

    /// <summary>Source changed its edit state or text: show, hide and fill the editor; when an edit opens, start its undo
    /// history and bring the active cell into view.</summary>
    private void OnEditStateChanged(object? sender, EventArgs e)
    {
        bool wasVisible = _editor.Visibility == Visibility.Visible;
        SyncEditor();
        if (!wasVisible && _editor.Visibility == Visibility.Visible && Source is { } src)
        {
            // An edit opened (from the grid or the formula bar). R49: the editor's history starts at the edit's start
            // text, which SyncEditor has just put there, so Undo never reaches the text of an earlier edit. The active
            // cell is scrolled into view so the editor is not clipped out of sight.
            ClearEditorHistory();
            ScrollIntoView(src.ActiveCell);
        }
        if (wasVisible && _editor.Visibility != Visibility.Visible && _editor.IsKeyboardFocusWithin)
            Focus(); // the edit was committed or cancelled from the editor: the grid takes the keys again
    }

    private void ClearEditorHistory()
    {
        _editor.IsUndoEnabled = false;
        _editor.IsUndoEnabled = true;
    }

    private void SyncEditor()
    {
        IGridEditSource? edit = EditSource;
        bool editing = edit is { IsEditing: true };
        if (editing && _editor.Text != edit!.EditText)
        {
            _syncingEditor = true;
            try { _editor.Text = edit.EditText; }
            finally { _syncingEditor = false; }
        }
        var visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        if (_editor.Visibility != visibility)
        {
            _editor.Visibility = visibility;
            InvalidateMeasure();
        }
        InvalidateArrange();
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingEditor || EditSource is not { } edit || _editor.Visibility != Visibility.Visible) return;
        edit.EditText = _editor.Text; // the formula bar follows
    }

    /// <summary>Enter and Tab commit and move; Esc cancels (§11.4). Handled before the TextBox and keyboard navigation see
    /// them. Keys the IME is processing arrive as <see cref="Key.ImeProcessed"/> and are left alone.</summary>
    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (EditSource is not { IsEditing: true } edit) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Enter: edit.CommitEdit(0, shift ? -1 : 1); break;
            case Key.Tab: edit.CommitEdit(shift ? -1 : 1, 0); break;
            case Key.Escape: edit.CancelEdit(); break;
            default: return;
        }
        e.Handled = true;
        Focus();
    }

    /// <summary>The editor's rectangle: the active cell, widened to the text up to the right edge of the control.</summary>
    private void ArrangeEditor(Size finalSize)
    {
        if (_editor.Visibility != Visibility.Visible || Source is not { } src)
        {
            _editor.Arrange(new Rect(0, 0, 0, 0));
            return;
        }
        var cell = src.ActiveCell;
        double left = GridMetrics.ColumnLeft(cell.Column, _firstColumn) + 1;
        double top = GridMetrics.RowTop(cell.Row, _firstRow) + 1;
        double available = Math.Max(GridMetrics.ColumnWidth - 2, finalSize.Width - left);
        double width = Math.Min(available, Math.Max(GridMetrics.ColumnWidth - 2, _editor.DesiredSize.Width + 8));
        var rect = new Rect(left, top, width, GridMetrics.RowHeight - 2);
        _editor.Arrange(rect);
        // Clip to the cell area so an editor scrolled under the headers or out of view is not drawn over them.
        var cellArea = new Rect(GridMetrics.RowHeaderWidth, GridMetrics.ColumnHeaderHeight,
            Math.Max(0, finalSize.Width - GridMetrics.RowHeaderWidth), Math.Max(0, finalSize.Height - GridMetrics.ColumnHeaderHeight));
        cellArea.Intersect(rect);
        _editor.Clip = cellArea.IsEmpty
            ? Geometry.Empty
            : new RectangleGeometry(new Rect(cellArea.X - left, cellArea.Y - top, cellArea.Width, cellArea.Height));
    }

    /// <summary>The editor rectangle in control coordinates (test hook).</summary>
    internal Rect EditorBounds => _editor.Visibility == Visibility.Visible
        ? new Rect(VisualTreeHelper.GetOffset(_editor).X, VisualTreeHelper.GetOffset(_editor).Y, _editor.RenderSize.Width, _editor.RenderSize.Height)
        : Rect.Empty;
}
