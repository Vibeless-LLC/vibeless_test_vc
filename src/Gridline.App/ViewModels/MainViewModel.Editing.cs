using Gridline.App.Controls;
using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.App.ViewModels;

/// <summary>
/// Cell editing, the formula bar and the name box (§11.4, R49). One pending text serves both the in-cell editor and the
/// formula bar (<see cref="FormulaBarText"/>), so the two are always in sync; see DECISIONS D-38.
/// </summary>
public sealed partial class MainViewModel : IGridEditSource
{
    private bool _isEditing;

    /// <summary>True while an edit of the active cell is open (started by typing, F2 or typing in the formula bar).</summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (!SetProperty(ref _isEditing, value)) return;
            RaiseUndoRedoCanExecuteChanged();
            RaiseClipboardCanExecuteChanged(); // Copy and Paste act on the selection only without an open edit
            EditStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>R49: the history of the text box the edit is typed in, set by the view. Null in ViewModel tests that do not
    /// need it (Undo/Redo are then unavailable while editing).</summary>
    public IEditTextHistory? EditTextHistory { get; set; }

    public event EventHandler? EditStateChanged;

    string IGridEditSource.EditText
    {
        get => FormulaBarText;
        set => FormulaBarText = value;
    }

    /// <summary>Formula bar text (§11.4): the active cell's input, or the pending text while an edit is open. Typing a
    /// different text into it opens an edit.</summary>
    public string FormulaBarText
    {
        get => _formulaBarText;
        set
        {
            value ??= "";
            if (value == _formulaBarText) return;
            if (!IsReady) // before startup has completed nothing opens an edit (D-49); the box shows the text again
            {
                OnPropertyChanged();
                return;
            }
            _formulaBarText = value;
            OnPropertyChanged();
            if (!_isEditing) IsEditing = true; // raises EditStateChanged
            else EditStateChanged?.Invoke(this, EventArgs.Empty);
            RaiseUndoRedoCanExecuteChanged(); // the edit box's own history changed
        }
    }

    /// <summary>Name box text (§11.4): the active address, unqualified (R88); the user may type an address, optionally
    /// sheet-qualified, and press Enter (<see cref="NavigateToNameBox"/>).</summary>
    public string NameBoxText
    {
        get => _nameBoxText;
        set => SetProperty(ref _nameBoxText, value ?? "");
    }

    public void BeginEdit(string? replacement)
    {
        if (_isEditing || !IsReady) return;
        string text = replacement ?? _workbook.GetInput(_active.ToString());
        if (text.Length > Workbook.MaxInputLength) text = text[..Workbook.MaxInputLength];
        SetFormulaBarTextSilently(text);
        IsEditing = true;
    }

    public void CommitEdit(int columnDelta, int rowDelta)
    {
        if (_isEditing && !TryCommit()) return;
        MoveActiveCell(columnDelta, rowDelta, extend: false);
    }

    /// <summary>Commits an open edit and keeps the active cell (formula bar Enter, focus leaving the edit for the grid or the
    /// name box). Returns false when the workbook refused the input; the edit then stays open.</summary>
    public bool CommitPendingEdit() => !_isEditing || TryCommit();

    public void CancelEdit()
    {
        IsEditing = false;
        RefreshContentDerived(); // the formula bar shows the input again
    }

    public void ClearSelection()
    {
        if (_isEditing || !IsReady) return;
        var used = SelectionSummary.UsedCellsIn(_workbook, Selection);
        if (used.Count == 0) return; // no edits, so no undo step (§9.3)
        var edits = new KeyValuePair<string, string>[used.Count];
        for (int i = 0; i < used.Count; i++) edits[i] = new(used[i], "");
        _workbook.ApplyBatch(edits); // one undo step; CellsChanged refreshes the grid and bars
    }

    /// <summary>Name box Enter (§11.4): a valid address (<c>$</c> markers and lower case accepted, surrounding spaces
    /// ignored) commits any open edit and becomes the active cell, scrolled into view; the box then shows the canonical
    /// address. An invalid address reverts the box. A sheet-qualified address (R82 prefix rules, <c>Data!B2</c>,
    /// <c>'My Sheet'!A1</c>, case-insensitive) activates that sheet and selects the cell (R88); an unknown sheet shows a
    /// message and changes nothing. Returns true when it navigated.</summary>
    public bool NavigateToNameBox()
    {
        string text = _nameBoxText.Trim();
        if (!SheetName.TrySplitQualified(text, out string? sheet, out string rest)
            || !CellAddress.TryParse(rest, out CellAddress target))
        {
            RevertNameBox();
            return false;
        }
        if (sheet is not null && !string.Equals(sheet, _workbook.ActiveSheet, StringComparison.OrdinalIgnoreCase))
        {
            if (FindSheetName(sheet) is not { } defined)
            {
                _dialogs.ShowError("Gridline", $"There is no sheet named '{sheet}'.");
                RevertNameBox();
                return false;
            }
            if (!ActivateSheet(defined, target)) return false; // commits an open edit first, on its own sheet
            NameBoxText = _active.ToString();
            return true;
        }
        if (!CommitPendingEdit()) return false;
        SelectCell(target, extend: false);
        NameBoxText = _active.ToString(); // also when the target was already active
        return true;
    }

    /// <summary>Puts the active address back into the name box (Esc, invalid address, focus leaving it).</summary>
    public void RevertNameBox() => NameBoxText = _active.ToString();

    private bool TryCommit()
    {
        string text = _formulaBarText;
        IsEditing = false; // before SetCell, so the CellsChanged refresh shows the committed input
        try
        {
            _workbook.SetCell(_active.ToString(), text); // one undo step (§9.3); no step when unchanged
        }
        catch (ArgumentException ex) // over MaxInputLength; the boxes cap input, so this is a safety net
        {
            _dialogs.ShowError("Gridline", ex.Message);
            SetFormulaBarTextSilently(text);
            IsEditing = true;
            return false;
        }
        RefreshContentDerived();
        return true;
    }

    /// <summary>Sets the formula bar text without opening an edit.</summary>
    private void SetFormulaBarTextSilently(string text)
    {
        if (text == _formulaBarText) return;
        _formulaBarText = text;
        OnPropertyChanged(nameof(FormulaBarText));
        if (_isEditing) EditStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseUndoRedoCanExecuteChanged()
    {
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
    }

    // ---- R49: Undo/Redo act on the edit text box while an edit is open ----

    private bool CanUndoNow() => _isEditing ? EditTextHistory?.CanUndo ?? false : _workbook.CanUndo;

    private bool CanRedoNow() => _isEditing ? EditTextHistory?.CanRedo ?? false : _workbook.CanRedo;
}
