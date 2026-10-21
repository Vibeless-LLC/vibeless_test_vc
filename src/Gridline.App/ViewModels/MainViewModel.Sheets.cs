using System.Collections.ObjectModel;
using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.App.ViewModels;

/// <summary>
/// The sheet tab strip (CR1.8, R88): the tabs, activation, and the tab context menu's Add, Rename, Delete, Move Left and
/// Move Right, plus drag reordering. Activation selects A1 and makes no undo step, so the dirty state and title stay
/// (R80); every other command is one undo step (§9.3). The Core raises no sheets-changed event (R81), so the tabs are
/// refreshed from SheetNames and ActiveSheet after every command this ViewModel issues, after Undo and Redo, and whenever
/// a workbook is adopted (New, Open, Import CSV, recovery restore). An open edit is committed first; a refused edit or a
/// refused name changes nothing. See DECISIONS D-59.
/// </summary>
public sealed partial class MainViewModel
{
    private int _activeSheetIndex;
    private bool _refreshingTabs;

    /// <summary>The tabs, in sheet order (<c>SheetTabs</c>; each tab <c>SheetTab_&lt;index&gt;</c>).</summary>
    public ObservableCollection<SheetTabViewModel> SheetTabs { get; } = [];

    /// <summary>The active sheet's index in <see cref="SheetTabs"/>. Setting it (a tab click) activates that sheet; an
    /// index outside the tabs is ignored (the strip reports -1 while its items change).</summary>
    public int ActiveSheetIndex
    {
        get => _activeSheetIndex;
        set
        {
            if (_refreshingTabs || value == _activeSheetIndex || value < 0 || value >= SheetTabs.Count) return;
            if (!ActivateSheet(value)) OnPropertyChanged(); // refused (an edit could not be committed): the strip reverts
        }
    }

    /// <summary>The active sheet's name in its defined casing.</summary>
    public string ActiveSheetName => _workbook.ActiveSheet;

    /// <summary>Tab context menu Add (<c>ContextAddSheet</c>).</summary>
    public RelayCommand AddSheetCommand { get; private set; } = null!;

    /// <summary>Tab context menu Rename (<c>ContextRenameSheet</c>).</summary>
    public RelayCommand RenameSheetCommand { get; private set; } = null!;

    /// <summary>Tab context menu Delete (<c>ContextDeleteSheet</c>); disabled on the last sheet (R88).</summary>
    public RelayCommand DeleteSheetCommand { get; private set; } = null!;

    /// <summary>Tab context menu Move Left (<c>ContextMoveSheetLeft</c>); disabled on the first tab (R88).</summary>
    public RelayCommand MoveSheetLeftCommand { get; private set; } = null!;

    /// <summary>Tab context menu Move Right (<c>ContextMoveSheetRight</c>); disabled on the last tab (R88).</summary>
    public RelayCommand MoveSheetRightCommand { get; private set; } = null!;

    private void CreateSheetCommands()
    {
        AddSheetCommand = new RelayCommand(() => AddSheet(), Ready(() => SheetTabs.Count < Workbook.MaxSheetCount));
        RenameSheetCommand = new RelayCommand(() => RenameActiveSheet(), Ready());
        DeleteSheetCommand = new RelayCommand(() => DeleteActiveSheet(), Ready(() => SheetTabs.Count > 1));
        MoveSheetLeftCommand = new RelayCommand(() => MoveActiveSheet(-1), Ready(() => _activeSheetIndex > 0));
        MoveSheetRightCommand = new RelayCommand(() => MoveActiveSheet(+1), Ready(() => _activeSheetIndex < SheetTabs.Count - 1));
    }

    /// <summary>Activates the sheet at <paramref name="index"/> and selects A1 (R88). No undo step; the dirty state and
    /// title are unchanged (R80). Returns false when not ready, the index is outside the tabs, or an open edit was refused.</summary>
    public bool ActivateSheet(int index)
    {
        var names = _workbook.SheetNames;
        if (index < 0 || index >= names.Count) return false;
        return ActivateSheet(names[index], new CellAddress(1, 1));
    }

    /// <summary>Commits an open edit (to the sheet it belongs to), then makes <paramref name="name"/> the active sheet with
    /// <paramref name="cell"/> selected.</summary>
    private bool ActivateSheet(string name, CellAddress cell)
    {
        if (!IsReady || !CommitPendingEdit()) return false;
        _workbook.ActiveSheet = name;
        ShowActiveSheet(cell);
        return true;
    }

    /// <summary>Add (R88): appends the first free <c>Sheet&lt;n&gt;</c> (smallest n ≥ 1, case-insensitive) and activates
    /// it. One undo step. Returns true when the sheet was added.</summary>
    public bool AddSheet()
    {
        if (!IsReady || !CommitPendingEdit()) return false;
        string name = NextFreeSheetName(_workbook.SheetNames);
        try
        {
            _workbook.AddSheet(name);
        }
        catch (InvalidOperationException ex) // 255 sheets (R78); the command is disabled then, so this is a safety net
        {
            _dialogs.ShowError("Gridline", ex.Message);
            return false;
        }
        _workbook.ActiveSheet = name;
        ShowActiveSheet(new CellAddress(1, 1));
        RefreshDocumentState();
        return true;
    }

    /// <summary>Rename (R88): asks for a new name for the active sheet (<c>RenameSheetBox</c>). An invalid or duplicate name
    /// shows a message and changes nothing; the same name is a no-op (R79). One undo step. Returns true when renamed.</summary>
    public bool RenameActiveSheet()
    {
        if (!IsReady || !CommitPendingEdit()) return false;
        string oldName = _workbook.ActiveSheet;
        string? newName = _dialogs.PromptSheetName(oldName);
        if (newName is null) return false;
        // R47: RenameSheet raises CellsChanged synchronously under the new name, so the copied sheet follows first.
        string? copiedBefore = _copiedSheet;
        if (copiedBefore is not null && string.Equals(copiedBefore, oldName, StringComparison.OrdinalIgnoreCase))
            _copiedSheet = newName;
        try
        {
            _workbook.RenameSheet(oldName, newName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) // R78, R85: nothing changed
        {
            _copiedSheet = copiedBefore;
            _dialogs.ShowError("Gridline", ex.Message);
            return false;
        }
        if (_copiedSheet is not null && string.Equals(_copiedSheet, newName, StringComparison.OrdinalIgnoreCase))
            _copiedSheet = _workbook.ActiveSheet; // still copied: the canonical new name
        AfterSheetCommand();
        return true;
    }

    /// <summary>Delete (R88): deletes the active sheet; the sheet now at its index (or the last) becomes active (R80) with
    /// A1 selected. Unavailable on the last sheet. One undo step. Returns true when deleted.</summary>
    public bool DeleteActiveSheet()
    {
        if (!IsReady || SheetTabs.Count <= 1 || !CommitPendingEdit()) return false;
        try
        {
            _workbook.DeleteSheet(_workbook.ActiveSheet);
        }
        catch (InvalidOperationException ex) // the last sheet, or a rewrite over MaxInputLength (R85): nothing changed
        {
            _dialogs.ShowError("Gridline", ex.Message);
            return false;
        }
        ShowActiveSheet(new CellAddress(1, 1));
        RefreshDocumentState();
        return true;
    }

    /// <summary>Move Left (-1) or Move Right (+1) of the active sheet. One undo step; the sheet stays active and the
    /// selection stays.</summary>
    private bool MoveActiveSheet(int delta) => MoveSheet(_activeSheetIndex, _activeSheetIndex + delta);

    /// <summary>Moves the sheet at <paramref name="from"/> to <paramref name="to"/> (Move Left/Right and dragging a tab,
    /// R88): one MoveSheet call, one undo step. The active sheet stays the same sheet. Returns true when it moved.</summary>
    public bool MoveSheet(int from, int to)
    {
        var names = _workbook.SheetNames;
        if (!IsReady || from < 0 || from >= names.Count || to < 0 || to >= names.Count || from == to) return false;
        if (!CommitPendingEdit()) return false;
        _workbook.MoveSheet(names[from], to);
        AfterSheetCommand();
        return true;
    }

    /// <summary>R88: the first free <c>Sheet&lt;n&gt;</c>, smallest n ≥ 1, compared case-insensitively.</summary>
    internal static string NextFreeSheetName(IReadOnlyList<string> names)
    {
        var taken = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
        {
            string candidate = "Sheet" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>The active sheet was replaced: the selection becomes <paramref name="cell"/> (a cell selection), the tabs,
    /// grid, bars and name box show the new sheet, and the grid scrolls to the cell.</summary>
    private void ShowActiveSheet(CellAddress cell)
    {
        _anchor = _active = cell;
        _headerAxis = null;
        RefreshSheetTabs();
        ContentChanged?.Invoke(this, EventArgs.Empty);
        OnSelectionMoved();
    }

    /// <summary>After a rename or move: the active sheet is the same sheet, so the selection stays; formulas may have been
    /// rewritten (R85), so the grid, formula bar and status bar are refreshed.</summary>
    private void AfterSheetCommand()
    {
        RefreshSheetTabs();
        ContentChanged?.Invoke(this, EventArgs.Empty);
        RefreshContentDerived();
        RefreshDocumentState();
    }

    /// <summary>After Undo or Redo (R80, R88): the tabs are refreshed. When the step changed the number of sheets and
    /// another sheet became active (undo of an Add of the active sheet, redo of its Delete) that sheet is shown with A1
    /// selected; a step that renamed or moved sheets, or restored another one, keeps the selection but repaints.</summary>
    private void AfterHistoryStep(IReadOnlyList<string> namesBefore, string activeBefore)
    {
        var names = _workbook.SheetNames;
        if (names.SequenceEqual(namesBefore, StringComparer.Ordinal)) return; // a cell step: CellsChanged has refreshed
        if (names.Count != namesBefore.Count && !string.Equals(activeBefore, _workbook.ActiveSheet, StringComparison.Ordinal))
        {
            ShowActiveSheet(new CellAddress(1, 1));
            return;
        }
        RefreshSheetTabs();
        ContentChanged?.Invoke(this, EventArgs.Empty);
        RefreshContentDerived();
    }

    /// <summary>Updates the tabs in place from <see cref="Workbook.SheetNames"/> and <see cref="Workbook.ActiveSheet"/>,
    /// forgets a copied range whose sheet no longer exists (R47), and refreshes the sheet commands' availability.</summary>
    private void RefreshSheetTabs()
    {
        var names = _workbook.SheetNames;
        _refreshingTabs = true;
        try
        {
            // The index first, and announced before the items change, so the strip has moved its selection off a tab that
            // is about to be removed; the strip's own -1 while its items change is ignored (the flag).
            _activeSheetIndex = _workbook.ActiveSheetIndex;
            if (_activeSheetIndex < SheetTabs.Count) OnPropertyChanged(nameof(ActiveSheetIndex));
            for (int i = 0; i < names.Count; i++)
            {
                if (i < SheetTabs.Count) SheetTabs[i].Name = names[i];
                else SheetTabs.Add(new SheetTabViewModel(i, names[i]));
            }
            while (SheetTabs.Count > names.Count) SheetTabs.RemoveAt(SheetTabs.Count - 1);
        }
        finally
        {
            _refreshingTabs = false;
        }
        OnPropertyChanged(nameof(ActiveSheetIndex));
        OnPropertyChanged(nameof(ActiveSheetName));
        if (_copiedSheet is not null && FindSheetName(_copiedSheet) is null) ForgetInternalCopy();
        RaiseSheetCanExecuteChanged();
    }

    private void RaiseSheetCanExecuteChanged()
    {
        AddSheetCommand.RaiseCanExecuteChanged();
        RenameSheetCommand.RaiseCanExecuteChanged();
        DeleteSheetCommand.RaiseCanExecuteChanged();
        MoveSheetLeftCommand.RaiseCanExecuteChanged();
        MoveSheetRightCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The defined casing of the sheet named <paramref name="name"/> (case-insensitive), or null.</summary>
    private string? FindSheetName(string name)
    {
        foreach (string s in _workbook.SheetNames)
            if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }
}
