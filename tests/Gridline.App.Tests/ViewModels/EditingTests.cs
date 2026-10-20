using Gridline.App.Controls;
using Gridline.App.ViewModels;
using Gridline.Core;
using Gridline.Core.Addressing;
using static Gridline.App.Tests.TestSupport;

namespace Gridline.App.Tests.ViewModels;

/// <summary>§11.4 editing through the ViewModel: each row of the table, the grid edges, Delete, name box and formula bar,
/// and R49 (Undo/Redo act on the edit box while an edit is open).</summary>
public class EditingTests
{
    private static CellAddress A(string s) => CellAddress.Parse(s);

    private sealed class FakeHistory : IEditTextHistory
    {
        public bool CanUndo { get; set; }
        public bool CanRedo { get; set; }
        public int Undos { get; private set; }
        public int Redos { get; private set; }
        public void Undo() => Undos++;
        public void Redo() => Redos++;
    }

    private static int UndoDepth(Workbook wb)
    {
        int n = 0;
        while (wb.Undo()) n++;
        for (int i = 0; i < n; i++) wb.Redo();
        return n;
    }

    // ---- table rows ----

    [Fact]
    public void TypingAPrintableCharacter_StartsEditingWithThatCharacter_ReplacingTheInput()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("A1", "old");
        vm.BeginEdit("x");
        Assert.True(vm.IsEditing);
        Assert.Equal("x", vm.FormulaBarText);
        Assert.Equal("old", vm.Workbook.GetInput("A1")); // nothing committed yet
    }

    [Fact]
    public void F2_EditsTheExistingInput()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("A1", "=1+2");
        vm.BeginEdit(null);
        Assert.True(vm.IsEditing);
        Assert.Equal("=1+2", vm.FormulaBarText);
    }

    [Fact]
    public void Enter_CommitsThroughSetCell_AndMovesDown_OneUndoStep()
    {
        var vm = NewViewModel();
        vm.BeginEdit("4");
        ((IGridEditSource)vm).EditText = "42";
        vm.CommitEdit(0, 1);
        Assert.False(vm.IsEditing);
        Assert.Equal("42", vm.Workbook.GetInput("A1"));
        Assert.Equal("A2", vm.ActiveAddress);
        Assert.Equal("", vm.FormulaBarText);
        Assert.Equal("BLANK", vm.SelectionValue);
        Assert.Equal(1, UndoDepth(vm.Workbook));
        vm.SelectCell(A("A1"), extend: false);
        Assert.Equal("42", vm.FormulaBarText);
        Assert.Equal("N:42", vm.SelectionValue);
    }

    [Fact]
    public void Tab_CommitsAndMovesRight()
    {
        var vm = NewViewModel();
        vm.BeginEdit("hi");
        vm.CommitEdit(1, 0);
        Assert.Equal("hi", vm.Workbook.GetInput("A1"));
        Assert.Equal("B1", vm.ActiveAddress);
    }

    [Fact]
    public void Esc_CancelsTheEdit_WorkbookUnchanged()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("A1", "keep");
        vm.BeginEdit("x");
        vm.CancelEdit();
        Assert.False(vm.IsEditing);
        Assert.Equal("keep", vm.Workbook.GetInput("A1"));
        Assert.Equal("keep", vm.FormulaBarText);
        Assert.Equal(1, UndoDepth(vm.Workbook));
    }

    [Fact]
    public void Delete_ClearsUsedCellsOfTheSelection_AsOneUndoStep()
    {
        var vm = NewViewModel();
        vm.Workbook.ApplyBatch([new("A1", "1"), new("B2", "2"), new("C3", "3"), new("D4", "outside")]);
        vm.SelectCell(A("A1"), extend: false);
        vm.SelectCell(A("C3"), extend: true);
        int before = UndoDepth(vm.Workbook);
        vm.ClearSelection();
        Assert.Equal("", vm.Workbook.GetInput("A1"));
        Assert.Equal("", vm.Workbook.GetInput("B2"));
        Assert.Equal("", vm.Workbook.GetInput("C3"));
        Assert.Equal("outside", vm.Workbook.GetInput("D4"));
        Assert.Equal(before + 1, UndoDepth(vm.Workbook));
        Assert.Equal("0", vm.SelectionSum);
        vm.UndoCommand.Execute(null);
        Assert.Equal("2", vm.Workbook.GetInput("B2"));
        Assert.Equal("6", vm.SelectionSum);
    }

    [Fact]
    public void Delete_OverUnusedCells_DoesNothing_NoUndoStep()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("Z50", "x");
        vm.SelectCell(A("B2"), extend: false);
        vm.SelectCell(A("D9"), extend: true);
        int events = 0;
        vm.Workbook.CellsChanged += (_, _) => events++;
        vm.ClearSelection();
        Assert.Equal(0, events);
        Assert.Equal(1, UndoDepth(vm.Workbook));
        Assert.True(vm.IsDirty); // from Z50 only
    }

    [Fact]
    public void Delete_OverTheWholeGrid_ClearsOnlyUsedCells()
    {
        var vm = NewViewModel();
        vm.Workbook.ApplyBatch([new("A1", "1"), new("ZZ100000", "2"), new("M500", "=A1")]);
        vm.SelectCell(A("A1"), extend: false);
        vm.SelectCell(A("ZZ100000"), extend: true); // 70.2 million cells
        CellsChangedEventArgs? args = null;
        vm.Workbook.CellsChanged += (_, e) => args = e;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        vm.ClearSelection();
        sw.Stop();
        Assert.Empty(vm.Workbook.GetUsedAddresses());
        Assert.NotNull(args);
        Assert.Equal(3, args!.Addresses.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"{sw.Elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public void UsedCellsIn_BothStrategiesAgree()
    {
        var wb = new Workbook();
        wb.ApplyBatch([new("B2", "1"), new("C5000", "x"), new("A1", "y"), new("D3", "")]);
        var small = CellRange.Parse("A1:D3"); // probed
        var large = CellRange.Parse("A1:D5000"); // 20,000 cells > 3 used: filtered
        Assert.Equal(["A1", "B2"], SelectionSummary.UsedCellsIn(wb, small));
        Assert.Equal(["A1", "B2", "C5000"], SelectionSummary.UsedCellsIn(wb, large));
    }

    // ---- edges ----

    [Fact]
    public void Enter_OnRow100000_CommitsAndStaysInPlace()
    {
        var vm = NewViewModel();
        vm.SelectCell(A("C100000"), extend: false);
        vm.BeginEdit("7");
        vm.CommitEdit(0, 1);
        Assert.Equal("7", vm.Workbook.GetInput("C100000"));
        Assert.Equal("C100000", vm.ActiveAddress);
        Assert.Equal("7", vm.FormulaBarText);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public void Tab_OnColumnZZ_CommitsAndStaysInPlace()
    {
        var vm = NewViewModel();
        vm.SelectCell(A("ZZ3"), extend: false);
        vm.BeginEdit("t");
        vm.CommitEdit(1, 0);
        Assert.Equal("t", vm.Workbook.GetInput("ZZ3"));
        Assert.Equal("ZZ3", vm.ActiveAddress);
    }

    [Fact]
    public void Commit_RecalculatesDependents_AndRefreshesStatusBar()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("A2", "=A1*2");
        vm.SelectCell(A("A1"), extend: false);
        vm.SelectCell(A("A2"), extend: true);
        vm.SelectCell(A("A1"), extend: false);
        vm.BeginEdit("5");
        int content = 0;
        vm.ContentChanged += (_, _) => content++;
        vm.CommitEdit(0, 1);
        Assert.Equal("N:10", vm.SelectionValue);
        Assert.Equal(1, content);
    }

    [Fact]
    public void Commit_OfUnchangedText_CreatesNoUndoStep()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("A1", "same");
        vm.BeginEdit(null);
        vm.CommitEdit(0, 1);
        Assert.Equal(1, UndoDepth(vm.Workbook));
    }

    [Fact]
    public void EmptyCommit_ClearsTheCell()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("A1", "x");
        vm.BeginEdit(null);
        ((IGridEditSource)vm).EditText = "";
        vm.CommitEdit(0, 0);
        Assert.Equal("", vm.Workbook.GetInput("A1"));
        Assert.Equal("BLANK", vm.SelectionValue);
    }

    [Fact]
    public void SelectingAnotherCell_WhileEditing_CommitsToTheEditedCell()
    {
        var vm = NewViewModel();
        vm.SelectCell(A("B2"), extend: false);
        vm.BeginEdit("9");
        vm.SelectCell(A("D4"), extend: false);
        Assert.Equal("9", vm.Workbook.GetInput("B2"));
        Assert.Equal("D4", vm.ActiveAddress);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public void BeginEdit_WhileEditing_IsIgnored()
    {
        var vm = NewViewModel();
        vm.BeginEdit("a");
        vm.BeginEdit("b");
        Assert.Equal("a", vm.FormulaBarText);
    }

    [Fact]
    public void EditStateChanged_RaisedOnBeginTextAndEnd()
    {
        var vm = NewViewModel();
        int n = 0;
        vm.EditStateChanged += (_, _) => n++;
        vm.BeginEdit("a");
        Assert.True(n >= 1);
        int afterBegin = n;
        ((IGridEditSource)vm).EditText = "ab";
        Assert.Equal(afterBegin + 1, n);
        vm.CancelEdit();
        Assert.True(n > afterBegin + 1);
    }

    [Fact]
    public void ContentChange_WhileEditing_KeepsThePendingText()
    {
        var vm = NewViewModel();
        vm.BeginEdit("pending");
        vm.Workbook.SetCell("A1", "external");
        Assert.True(vm.IsEditing);
        Assert.Equal("pending", vm.FormulaBarText);
    }

    [Fact]
    public void New_WhileEditing_CommitsThenPrompts_DontSaveClosesTheEdit()
    {
        var dialogs = new FakeDialogService { DefaultPromptAnswer = Gridline.App.Services.UnsavedChangesChoice.DontSave };
        var vm = NewViewModel(dialogs);
        vm.BeginEdit("x");
        vm.NewCommand.Execute(null); // the typed text is a change (D-39), so the prompt is shown
        Assert.Equal(["Untitled"], dialogs.Prompts);
        Assert.False(vm.IsEditing);
        Assert.Equal("", vm.FormulaBarText);
        Assert.Empty(vm.Workbook.GetUsedAddresses());
    }

    // ---- formula bar ----

    [Fact]
    public void FormulaBar_TypingOpensAnEdit_EnterCommitsAndStays()
    {
        var vm = NewViewModel();
        vm.SelectCell(A("B3"), extend: false);
        vm.FormulaBarText = "=2*3";
        Assert.True(vm.IsEditing);
        Assert.True(vm.CommitPendingEdit());
        Assert.False(vm.IsEditing);
        Assert.Equal("=2*3", vm.Workbook.GetInput("B3"));
        Assert.Equal("B3", vm.ActiveAddress);
        Assert.Equal("N:6", vm.SelectionValue);
        Assert.Equal("=2*3", vm.FormulaBarText);
        Assert.Equal(1, UndoDepth(vm.Workbook));
    }

    [Fact]
    public void FormulaBar_EscReverts()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("A1", "orig");
        vm.FormulaBarText = "changed";
        vm.CancelEdit();
        Assert.False(vm.IsEditing);
        Assert.Equal("orig", vm.FormulaBarText);
        Assert.Equal("orig", vm.Workbook.GetInput("A1"));
    }

    [Fact]
    public void FormulaBar_SameTextAsInput_DoesNotOpenAnEdit()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("A1", "x");
        vm.FormulaBarText = "x";
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public void FormulaBar_AndInCellEditor_ShareThePendingText()
    {
        var vm = NewViewModel();
        IGridEditSource edit = vm;
        vm.BeginEdit("a");
        vm.FormulaBarText = "ab"; // typed in the formula bar
        Assert.Equal("ab", edit.EditText);
        edit.EditText = "abc"; // typed in the cell
        Assert.Equal("abc", vm.FormulaBarText);
    }

    [Fact]
    public void Commit_OverMaxInputLength_ShowsErrorAndKeepsEditing()
    {
        var dialogs = new FakeDialogService();
        var vm = NewViewModel(dialogs);
        vm.FormulaBarText = new string('x', Workbook.MaxInputLength + 1); // the boxes cap input; this is the safety net
        Assert.False(vm.CommitPendingEdit());
        Assert.True(vm.IsEditing);
        Assert.Single(dialogs.Errors);
        Assert.Equal("", vm.Workbook.GetInput("A1"));
    }

    [Fact]
    public void Commit_AtMaxInputLength_Succeeds()
    {
        var vm = NewViewModel();
        string text = new('y', Workbook.MaxInputLength);
        vm.BeginEdit(text);
        vm.CommitEdit(0, 0);
        Assert.Equal(text, vm.Workbook.GetInput("A1"));
    }

    // ---- name box ----

    [Theory]
    [InlineData("C7", "C7")]
    [InlineData("$a$1", "A1")]
    [InlineData("zz100000", "ZZ100000")]
    [InlineData("b$2", "B2")]
    [InlineData(" d4 ", "D4")]
    public void NameBox_ValidAddress_Navigates_AndShowsCanonicalAddress(string typed, string expected)
    {
        var vm = NewViewModel();
        vm.SelectCell(A("B5"), extend: false);
        int moved = 0;
        vm.SelectionChanged += (_, _) => moved++;
        vm.NameBoxText = typed;
        Assert.True(vm.NavigateToNameBox());
        Assert.Equal(expected, vm.ActiveAddress);
        Assert.Equal(expected, vm.NameBoxText);
        Assert.Equal(CellRange.Parse(expected + ":" + expected), vm.Selection);
        Assert.Equal(1, moved); // SelectionChanged makes the grid scroll the target into view
    }

    [Theory]
    [InlineData("")]
    [InlineData("A0")]
    [InlineData("AAA1")]
    [InlineData("A100001")]
    [InlineData("hello")]
    [InlineData("A1:B2")]
    [InlineData("A01")]
    public void NameBox_InvalidAddress_RevertsTheBox(string typed)
    {
        var vm = NewViewModel();
        vm.SelectCell(A("C3"), extend: false);
        vm.NameBoxText = typed;
        Assert.False(vm.NavigateToNameBox());
        Assert.Equal("C3", vm.NameBoxText);
        Assert.Equal("C3", vm.ActiveAddress);
    }

    [Fact]
    public void NameBox_CurrentCellInOtherSpelling_ShowsCanonical()
    {
        var vm = NewViewModel();
        vm.NameBoxText = "$a1";
        Assert.True(vm.NavigateToNameBox());
        Assert.Equal("A1", vm.NameBoxText);
    }

    [Fact]
    public void NameBox_Navigation_CommitsAnOpenEdit()
    {
        var vm = NewViewModel();
        vm.BeginEdit("v");
        vm.NameBoxText = "E5";
        Assert.True(vm.NavigateToNameBox());
        Assert.Equal("v", vm.Workbook.GetInput("A1"));
        Assert.Equal("E5", vm.ActiveAddress);
    }

    [Fact]
    public void NameBox_Revert_RestoresTheActiveAddress()
    {
        var vm = NewViewModel();
        vm.NameBoxText = "partial";
        vm.RevertNameBox();
        Assert.Equal("A1", vm.NameBoxText);
    }

    // ---- R49 ----

    [Fact]
    public void UndoRedo_WhileEditing_ActOnTheEditBox_NotTheWorkbook()
    {
        var vm = NewViewModel();
        var history = new FakeHistory();
        vm.EditTextHistory = history;
        vm.Workbook.SetCell("B1", "1");
        vm.BeginEdit("x");
        Assert.False(vm.UndoCommand.CanExecute(null)); // the box has nothing to undo yet
        history.CanUndo = true;
        history.CanRedo = true;
        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        vm.RedoCommand.Execute(null);
        Assert.Equal(1, history.Undos);
        Assert.Equal(1, history.Redos);
        Assert.Equal("1", vm.Workbook.GetInput("B1")); // workbook untouched

        vm.CommitEdit(0, 0); // once committed they act on the workbook
        Assert.Equal("x", vm.Workbook.GetInput("A1"));
        vm.UndoCommand.Execute(null);
        Assert.Equal("", vm.Workbook.GetInput("A1"));
        Assert.Equal(1, history.Undos);
    }

    [Fact]
    public void UndoCanExecuteChanged_RaisedWhenEditingStartsAndEnds()
    {
        var vm = NewViewModel();
        int n = 0;
        vm.UndoCommand.CanExecuteChanged += (_, _) => n++;
        vm.BeginEdit("a");
        Assert.True(n > 0);
        int before = n;
        vm.CancelEdit();
        Assert.True(n > before);
    }
}
