using Gridline.App.Controls;
using Gridline.Core.Addressing;

namespace Gridline.App.ViewModels;

/// <summary>Active cell, selection and the text derived from them (§11.3, §11.4, §11.7); the grid's data source.</summary>
public sealed partial class MainViewModel : IGridDataSource
{
    private CellAddress _active = new(1, 1);
    private CellAddress _anchor = new(1, 1);
    private string _nameBoxText = "A1";
    private string _formulaBarText = "";
    private string _selectionValue = "BLANK";
    private string _selectionSum = "0";

    public event EventHandler? ContentChanged;
    public event EventHandler? SelectionChanged;

    public CellAddress ActiveCell => _active;

    /// <summary>The fixed corner of the selection (where Shift-extension started).</summary>
    public CellAddress SelectionAnchor => _anchor;

    /// <summary>The header axis of a whole-row or whole-column selection (R75), or null for a cell selection.</summary>
    private HeaderAxis? _headerAxis;

    /// <summary>The selected rectangle: anchor to active cell, or for a header selection (R75) the whole rows (A–ZZ)
    /// between the anchor's and the active cell's rows, or the whole columns (1–100000) between their columns.</summary>
    public CellRange Selection => _headerAxis switch
    {
        HeaderAxis.Rows => new(new CellAddress(1, _anchor.Row), new CellAddress(GridBounds.MaxColumns, _active.Row)),
        HeaderAxis.Columns => new(new CellAddress(_anchor.Column, 1), new CellAddress(_active.Column, GridBounds.MaxRows)),
        _ => new(_anchor, _active),
    };

    /// <summary>The header axis of the current whole-row or whole-column selection, or null (R75).</summary>
    public HeaderAxis? HeaderSelectionAxis => _headerAxis;

    /// <summary>The active cell's canonical address, e.g. <c>B2</c>.</summary>
    public string ActiveAddress => _active.ToString();

    /// <summary>§11.7 SelectionValue: the active cell's §8.3 canonical value (<c>N:5</c>, <c>BLANK</c>, ...).</summary>
    public string SelectionValue
    {
        get => _selectionValue;
        private set => SetProperty(ref _selectionValue, value);
    }

    /// <summary>§11.7 SelectionSum (R43).</summary>
    public string SelectionSum
    {
        get => _selectionSum;
        private set => SetProperty(ref _selectionSum, value);
    }

    public CellDisplay GetDisplay(int column, int row) =>
        CellDisplayFormatter.Format(_workbook.GetValue(new CellAddress(column, row).ToString()));

    public void MoveActiveCell(int columnDelta, int rowDelta, bool extend)
    {
        int column = (int)Math.Clamp((long)_active.Column + columnDelta, 1, GridBounds.MaxColumns);
        int row = (int)Math.Clamp((long)_active.Row + rowDelta, 1, GridBounds.MaxRows);
        SelectCell(new CellAddress(column, row), extend);
    }

    /// <summary>Makes <paramref name="cell"/> active. An open edit is committed to the cell it belongs to first (§11.4, D-38).</summary>
    /// <remarks>Extending (Shift+arrows, Shift+click, drag) keeps a whole-row or whole-column selection whole along its
    /// axis; anything else makes it a cell selection again (R75, D-49).</remarks>
    public void SelectCell(CellAddress cell, bool extend) => SetSelection(cell, extend, extend ? _headerAxis : null);

    /// <summary>The one place the selection changes (except Adopt's reset). Ignored until startup has completed (D-49).</summary>
    private void SetSelection(CellAddress active, bool extend, HeaderAxis? axis)
    {
        if (!IsReady) return;
        if (_isEditing && !TryCommit()) return;
        CellAddress anchor = extend ? _anchor : active;
        if (active == _active && anchor == _anchor && axis == _headerAxis) return;
        _active = active;
        _anchor = anchor;
        _headerAxis = axis;
        OnSelectionMoved();
    }

    public CellAddress GetLastUsedCell()
    {
        var used = _workbook.GetUsedAddresses();
        if (used.Count == 0) return new CellAddress(1, 1);
        int maxColumn = 1;
        foreach (string a in used) maxColumn = Math.Max(maxColumn, CellAddress.Parse(a).Column);
        int maxRow = CellAddress.Parse(used[^1]).Row; // row-major order (§9.1)
        return new CellAddress(maxColumn, maxRow);
    }

    /// <summary>The active cell or the selection changed (or was reset by Adopt): refresh everything derived from it and
    /// raise <see cref="SelectionChanged"/>, which makes the grid scroll the active cell into view.</summary>
    private void OnSelectionMoved()
    {
        NameBoxText = _active.ToString();
        RefreshContentDerived();
        OnPropertyChanged(nameof(ActiveCell));
        OnPropertyChanged(nameof(ActiveAddress));
        OnPropertyChanged(nameof(SelectionAnchor));
        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(HeaderSelectionAxis));
        RaiseStructureCanExecuteChanged();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Values may have changed but the selection did not: refresh the formula bar and status bar only. No
    /// SelectionChanged, so an edit or Undo never scrolls the grid back to the active cell.</summary>
    private void RefreshContentDerived()
    {
        string address = _active.ToString();
        if (!_isEditing) SetFormulaBarTextSilently(_workbook.GetInput(address)); // an open edit keeps its pending text
        SelectionValue = _workbook.GetValue(address).ToCanonicalString();
        SelectionSum = SelectionSummary.FormatSum(_workbook, Selection);
    }
}
