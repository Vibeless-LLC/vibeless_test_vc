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

    public CellRange Selection => new(_anchor, _active);

    /// <summary>The active cell's canonical address, e.g. <c>B2</c>.</summary>
    public string ActiveAddress => _active.ToString();

    /// <summary>Name box text (§11.4): the active address; editable, navigation on Enter arrives with the editing task.</summary>
    public string NameBoxText
    {
        get => _nameBoxText;
        set => SetProperty(ref _nameBoxText, value ?? "");
    }

    /// <summary>Formula bar text (§11.4): the active cell's input; editable, commit on Enter arrives with the editing task.</summary>
    public string FormulaBarText
    {
        get => _formulaBarText;
        set => SetProperty(ref _formulaBarText, value ?? "");
    }

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

    public void SelectCell(CellAddress cell, bool extend)
    {
        CellAddress anchor = extend ? _anchor : cell;
        if (cell == _active && anchor == _anchor) return;
        _active = cell;
        _anchor = anchor;
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
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Values may have changed but the selection did not: refresh the formula bar and status bar only. No
    /// SelectionChanged, so an edit or Undo never scrolls the grid back to the active cell.</summary>
    private void RefreshContentDerived()
    {
        string address = _active.ToString();
        FormulaBarText = _workbook.GetInput(address);
        SelectionValue = _workbook.GetValue(address).ToCanonicalString();
        SelectionSum = SelectionSummary.FormatSum(_workbook, Selection);
    }
}
