using Gridline.App.Controls;
using Gridline.Core.Addressing;

namespace Gridline.App.ViewModels;

/// <summary>
/// Whole-row and whole-column selection from the headers and the header context menu's Insert and Delete (§11.3, §11.5,
/// §9.5, R75). Insert and Delete act on as many rows or columns as are selected, at the first selected one, through
/// InsertRows/DeleteRows/InsertColumns/DeleteColumns, each one undo step (§9.3). An open edit is committed first; a
/// refused edit shows a message and changes nothing. See DECISIONS D-49.
/// </summary>
public sealed partial class MainViewModel : IGridHeaderSource
{
    /// <summary>Header context menu Insert (<c>ContextInsert</c>).</summary>
    public RelayCommand InsertRowsOrColumnsCommand { get; }

    /// <summary>Header context menu Delete (<c>ContextDelete</c>).</summary>
    public RelayCommand DeleteRowsOrColumnsCommand { get; }

    public bool HasHeaderSelection => _headerAxis is not null;

    public void SelectHeaders(HeaderAxis axis, int index, int activeOther, bool extend) =>
        SetSelection(HeaderCell(axis, index, activeOther), extend, axis);

    public bool PrepareHeaderMenu(HeaderAxis axis, int index, int activeOther)
    {
        if (!IsReady) return false;
        if (IsInsideWholeSelection(axis, index)) return true; // R75: the menu acts on the current selection
        SelectHeaders(axis, index, activeOther, extend: false);
        return IsInsideWholeSelection(axis, index); // false when the selection did not move (an edit is still open)
    }

    /// <summary>True when the selection is a whole-row (or whole-column) selection on <paramref name="axis"/> that
    /// contains <paramref name="index"/>.</summary>
    internal bool IsInsideWholeSelection(HeaderAxis axis, int index)
    {
        if (_headerAxis != axis) return false;
        CellRange s = Selection;
        return axis == HeaderAxis.Rows
            ? index >= s.TopLeft.Row && index <= s.BottomRight.Row
            : index >= s.TopLeft.Column && index <= s.BottomRight.Column;
    }

    /// <summary>Insert: as many empty rows or columns as are selected, at the first selected one. Returns true when the
    /// workbook accepted the edit.</summary>
    public bool InsertSelected() => ApplyStructureEdit(insert: true);

    /// <summary>Delete: the selected rows or columns. Returns true when the workbook accepted the edit.</summary>
    public bool DeleteSelected() => ApplyStructureEdit(insert: false);

    private bool CanEditStructure() => IsReady && _headerAxis is not null;

    private bool ApplyStructureEdit(bool insert)
    {
        if (!CanEditStructure()) return false;
        if (!CommitPendingEdit()) return false; // as New/Open/Exit commit first (D-39)
        HeaderAxis axis = _headerAxis!.Value;
        CellRange s = Selection;
        bool rows = axis == HeaderAxis.Rows;
        int first = rows ? s.TopLeft.Row : s.TopLeft.Column;
        int count = (rows ? s.BottomRight.Row : s.BottomRight.Column) - first + 1;
        try
        {
            switch (rows, insert)
            {
                case (true, true): _workbook.InsertRows(first, count); break;
                case (true, false): _workbook.DeleteRows(first, count); break;
                case (false, true): _workbook.InsertColumns(first, count); break;
                default: _workbook.DeleteColumns(first, count); break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            _dialogs.ShowError("Gridline", ex.Message); // nothing changed (§9.2)
            return false;
        }
        // The copied range's addresses no longer name the cells that were copied (§11.6, R47). CellsChanged has already
        // repainted and refreshed the bars when a value or input moved; the selection and active cell stay where they are.
        ForgetInternalCopy();
        // An edit that only adjusts names raises no CellsChanged (§9.2, R63, R64) but is still an undo step, so the title's
        // " *" and Undo/Redo availability are refreshed explicitly, as Undo() and Redo() do (§11.1).
        RefreshDocumentState();
        return true;
    }

    private static CellAddress HeaderCell(HeaderAxis axis, int index, int activeOther)
    {
        int Clamp(int v, int max) => Math.Clamp(v, 1, max);
        return axis == HeaderAxis.Rows
            ? new CellAddress(Clamp(activeOther, GridBounds.MaxColumns), Clamp(index, GridBounds.MaxRows))
            : new CellAddress(Clamp(index, GridBounds.MaxColumns), Clamp(activeOther, GridBounds.MaxRows));
    }

    private void RaiseStructureCanExecuteChanged()
    {
        InsertRowsOrColumnsCommand.RaiseCanExecuteChanged();
        DeleteRowsOrColumnsCommand.RaiseCanExecuteChanged();
    }
}
