using Gridline.App.Controls;
using Gridline.App.Services;
using Gridline.App.ViewModels;
using Gridline.Core;
using Gridline.Core.Addressing;
using Gridline.IO;
using static Gridline.App.Tests.TestSupport;

namespace Gridline.App.Tests.ViewModels;

/// <summary>The sheet tab strip and the qualified name box (CR1.8, R88, R80, R81, D-59): tabs from SheetNames, activation
/// (A1 selected, no undo step, dirty state and title unchanged, the grid and bars on the new sheet), Add with the first
/// free Sheet&lt;n&gt;, Rename with its messages, Delete, Move Left/Right and drag with their disabled states, Undo/Redo
/// refreshing the tabs, adopting a workbook, per-sheet scans, and the internal copy across sheets.</summary>
public sealed class SheetTabTests
{
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeClipboardService _clipboard = new();
    private readonly MainViewModel _vm;

    public SheetTabTests() => _vm = new MainViewModel(_dialogs, _clipboard);

    private Workbook Wb => _vm.Workbook;

    private string[] TabNames => [.. _vm.SheetTabs.Select(t => t.Name)];

    private void Go(string cell) => _vm.SelectCell(CellAddress.Parse(cell), extend: false);

    private void Select(string from, string to)
    {
        _vm.SelectCell(CellAddress.Parse(from), extend: false);
        _vm.SelectCell(CellAddress.Parse(to), extend: true);
    }

    // ---- the tabs ----

    [Fact]
    public void NewWorkbook_HasOneTab_Sheet1_Active_WithDeleteAndMovesDisabled()
    {
        var tab = Assert.Single(_vm.SheetTabs);
        Assert.Equal("Sheet1", tab.Name);
        Assert.Equal(0, tab.Index);
        Assert.Equal("SheetTab_0", tab.AutomationId);
        Assert.Equal(0, _vm.ActiveSheetIndex);
        Assert.Equal("Sheet1", _vm.ActiveSheetName);
        Assert.True(_vm.AddSheetCommand.CanExecute(null));
        Assert.True(_vm.RenameSheetCommand.CanExecute(null));
        Assert.False(_vm.DeleteSheetCommand.CanExecute(null)); // R88: the last sheet
        Assert.False(_vm.MoveSheetLeftCommand.CanExecute(null));
        Assert.False(_vm.MoveSheetRightCommand.CanExecute(null));
    }

    [Fact]
    public void Add_AppendsSheet2_ActivatesIt_SelectsA1_OneUndoStep_Dirty()
    {
        Wb.SetCell("A1", "5");
        Wb.MarkSaved();
        Go("C3");

        Assert.True(_vm.AddSheet());

        Assert.Equal(["Sheet1", "Sheet2"], TabNames);
        Assert.Equal(["SheetTab_0", "SheetTab_1"], _vm.SheetTabs.Select(t => t.AutomationId));
        Assert.Equal(1, _vm.ActiveSheetIndex);
        Assert.Equal("Sheet2", Wb.ActiveSheet);
        Assert.Equal("A1", _vm.ActiveAddress);
        Assert.Equal("A1", _vm.NameBoxText);
        Assert.Equal("BLANK", _vm.SelectionValue); // the new sheet's A1, not Sheet1's 5
        Assert.Equal("", _vm.FormulaBarText);
        Assert.True(_vm.IsDirty);
        Assert.Equal("Gridline - Untitled *", _vm.Title);
        Assert.True(_vm.DeleteSheetCommand.CanExecute(null));
        Assert.True(_vm.MoveSheetLeftCommand.CanExecute(null));
        Assert.False(_vm.MoveSheetRightCommand.CanExecute(null));

        _vm.UndoCommand.Execute(null); // exactly one step: back at the saved mark
        Assert.False(_vm.IsDirty);
        Assert.Equal(["Sheet1"], TabNames);
    }

    [Fact]
    public void Add_UsesTheFirstFreeSheetN_CaseInsensitively()
    {
        Assert.Equal("Sheet1", MainViewModel.NextFreeSheetName([]));
        Assert.Equal("Sheet2", MainViewModel.NextFreeSheetName(["Sheet1"]));
        Assert.Equal("Sheet1", MainViewModel.NextFreeSheetName(["Data", "sheet2"]));
        Assert.Equal("Sheet3", MainViewModel.NextFreeSheetName(["SHEET1", "sheet2", "Sheet4"]));

        _dialogs.SheetNameAnswer = "Data";
        _vm.RenameActiveSheet();       // Data
        _vm.AddSheet();                // Sheet1
        _vm.AddSheet();                // Sheet2
        Assert.Equal(["Data", "Sheet1", "Sheet2"], TabNames);
        Assert.Equal("Sheet2", _vm.ActiveSheetName);
    }

    // ---- activation ----

    [Fact]
    public void Activate_ShowsTheOtherSheet_SelectsA1_NoUndoStep_DirtyAndTitleUnchanged()
    {
        Wb.ApplyBatch([new("A1", "1"), new("B2", "=A1+1")]);
        Wb.AddSheet("Data");
        Wb.SetCell("Data!A1", "hello");
        Wb.SetCell("Data!B2", "=Sheet1!B2*10");
        Go("B2");
        Assert.Equal("=A1+1", _vm.FormulaBarText);
        bool dirty = _vm.IsDirty;
        string title = _vm.Title;
        bool canUndo = Wb.CanUndo;

        _vm.ActivateSheet(0); // an API AddSheet is not a ViewModel command: activating refreshes the strip (R81)
        Assert.Equal(["Sheet1", "Data"], TabNames);
        Go("B2");
        _vm.ActiveSheetIndex = 1; // the tab click

        Assert.Equal("Data", Wb.ActiveSheet);
        Assert.Equal(1, _vm.ActiveSheetIndex);
        Assert.Equal("A1", _vm.ActiveAddress);
        Assert.Equal("T:hello", _vm.SelectionValue);
        Assert.Equal("hello", _vm.FormulaBarText);
        Assert.Equal("hello", _vm.GetDisplay(1, 1).Text); // the grid reads the active sheet
        Go("B2");
        Assert.Equal("=Sheet1!B2*10", _vm.FormulaBarText);
        Assert.Equal("N:20", _vm.SelectionValue);
        Assert.Equal(dirty, _vm.IsDirty);
        Assert.Equal(title, _vm.Title);
        Assert.Equal(canUndo, Wb.CanUndo);

        _vm.ActiveSheetIndex = 0;
        Assert.Equal("Sheet1", Wb.ActiveSheet);
        Assert.Equal("A1", _vm.ActiveAddress);
        Assert.Equal("N:1", _vm.SelectionValue);
    }

    [Fact]
    public void Activate_RaisesContentAndSelectionChanged_AndIgnoresIndicesOutsideTheTabs()
    {
        _vm.AddSheet();
        int content = 0, selection = 0;
        _vm.ContentChanged += (_, _) => content++;
        _vm.SelectionChanged += (_, _) => selection++;

        _vm.ActiveSheetIndex = 0;
        Assert.True(content > 0);
        Assert.True(selection > 0);

        _vm.ActiveSheetIndex = -1; // the strip while its items change
        _vm.ActiveSheetIndex = 7;
        Assert.Equal(0, _vm.ActiveSheetIndex);
        Assert.False(_vm.ActivateSheet(5));
    }

    [Fact]
    public void Activate_CommitsAnOpenEdit_ToTheSheetItBelongsTo()
    {
        _vm.AddSheet(); // Sheet2 active
        _vm.BeginEdit("typed");

        _vm.ActiveSheetIndex = 0;

        Assert.False(_vm.IsEditing);
        Assert.Equal("typed", Wb.GetInput("Sheet2!A1"));
        Assert.Equal("", Wb.GetInput("Sheet1!A1"));
        Assert.Equal("Sheet1", Wb.ActiveSheet);
    }

    [Fact]
    public void SelectionSum_AndDelete_ScanTheActiveSheetOnly_OnBothStrategies()
    {
        var cells = new List<KeyValuePair<string, string>>();
        for (int r = 1; r <= 6000; r++) cells.Add(new("A" + r, "1"));
        Wb.ApplyBatch(cells); // Sheet1: 6,000 ones in column A
        _vm.AddSheet();
        Wb.ApplyBatch([new("A1", "7"), new("A3", "x")]);

        Select("A1", "A3"); // probed
        Assert.Equal("7", _vm.SelectionSum);
        _vm.SelectHeaders(HeaderAxis.Columns, 1, 1, extend: false); // 100,000 cells: the used cells are filtered
        Assert.Equal("7", _vm.SelectionSum);
        Assert.Equal(["A1", "A3"], SelectionSummary.UsedCellsIn(Wb, _vm.Selection));

        _vm.ClearSelection();
        Assert.Equal("", Wb.GetInput("Sheet2!A1"));
        Assert.Equal("1", Wb.GetInput("Sheet1!A1")); // the other sheet is untouched
        Assert.Equal(new CellAddress(1, 1), _vm.GetLastUsedCell()); // Ctrl+End on the empty active sheet

        _vm.ActiveSheetIndex = 0;
        Assert.Equal(new CellAddress(1, 6000), _vm.GetLastUsedCell());
        _vm.SelectHeaders(HeaderAxis.Columns, 1, 1, extend: false);
        Assert.Equal("6000", _vm.SelectionSum);
    }

    // ---- rename ----

    [Fact]
    public void Rename_RenamesTheActiveSheet_RewritesFormulas_OneUndoStep()
    {
        _vm.AddSheet(); // Sheet2
        Wb.SetCell("Sheet1!A1", "=Sheet2!A1+1");
        Go("B2");
        _dialogs.SheetNameAnswer = "My Data";

        Assert.True(_vm.RenameActiveSheet());

        Assert.Equal(["Sheet2"], _dialogs.SheetNamePrompts);
        Assert.Equal(["Sheet1", "My Data"], TabNames);
        Assert.Equal(1, _vm.ActiveSheetIndex);
        Assert.Equal("My Data", _vm.ActiveSheetName);
        Assert.Equal("B2", _vm.ActiveAddress); // the same sheet stays active with its selection
        Assert.Equal("='My Data'!A1+1", Wb.GetInput("Sheet1!A1"));
        Assert.True(_vm.IsDirty);

        _vm.UndoCommand.Execute(null);
        Assert.Equal(["Sheet1", "Sheet2"], TabNames);
        Assert.Equal("Sheet2", _vm.ActiveSheetName);
        Assert.Equal("=Sheet2!A1+1", Wb.GetInput("Sheet1!A1"));
        _vm.RedoCommand.Execute(null);
        Assert.Equal(["Sheet1", "My Data"], TabNames);
    }

    [Theory]
    [InlineData("a:b")]
    [InlineData("")]
    [InlineData("'quoted")]
    [InlineData("12345678901234567890123456789012")] // 32 characters
    [InlineData("sheet1")] // a duplicate, case-insensitively
    public void Rename_InvalidOrDuplicateName_ShowsAMessage_AndChangesNothing(string name)
    {
        _vm.AddSheet();
        Wb.MarkSaved();
        bool canUndo = Wb.CanUndo;
        _dialogs.SheetNameAnswer = name;

        Assert.False(_vm.RenameActiveSheet());

        var error = Assert.Single(_dialogs.Errors);
        Assert.Equal("Gridline", error.Title);
        Assert.NotEmpty(error.Message);
        Assert.Equal(["Sheet1", "Sheet2"], TabNames);
        Assert.Equal(canUndo, Wb.CanUndo);
        Assert.False(_vm.IsDirty);
    }

    [Fact]
    public void Rename_Cancelled_OrTheSameName_ChangesNothing()
    {
        _dialogs.SheetNameAnswer = null;
        Assert.False(_vm.RenameActiveSheet());
        _dialogs.SheetNameAnswer = "Sheet1"; // R79: a no-op, no undo step
        Assert.True(_vm.RenameActiveSheet());
        Assert.Empty(_dialogs.Errors);
        Assert.False(Wb.CanUndo);
        Assert.False(_vm.IsDirty);
        Assert.Equal(["Sheet1"], TabNames);
    }

    // ---- delete ----

    [Fact]
    public void Delete_RemovesTheActiveSheet_ActivatesItsNeighbour_AndUndoRestoresTheTabs()
    {
        _vm.AddSheet();
        _vm.AddSheet(); // Sheet1, Sheet2, Sheet3; Sheet3 active
        _vm.ActiveSheetIndex = 1;
        Wb.SetCell("B2", "on two");
        Go("B2");

        Assert.True(_vm.DeleteActiveSheet());

        Assert.Equal(["Sheet1", "Sheet3"], TabNames);
        Assert.Equal(["SheetTab_0", "SheetTab_1"], _vm.SheetTabs.Select(t => t.AutomationId));
        Assert.Equal(1, _vm.ActiveSheetIndex); // R80: the sheet now at the removed index
        Assert.Equal("Sheet3", _vm.ActiveSheetName);
        Assert.Equal("A1", _vm.ActiveAddress);

        _vm.UndoCommand.Execute(null);
        Assert.Equal(["Sheet1", "Sheet2", "Sheet3"], TabNames);
        Assert.Equal(3, _vm.SheetTabs.Count);
        Assert.Equal("SheetTab_2", _vm.SheetTabs[2].AutomationId);
        Assert.Equal("Sheet3", _vm.ActiveSheetName); // undo never otherwise changes the active sheet (R80)
        Assert.Equal(2, _vm.ActiveSheetIndex);
        Assert.Equal("on two", Wb.GetInput("Sheet2!B2"));
        Assert.True(_vm.DeleteSheetCommand.CanExecute(null));

        _vm.RedoCommand.Execute(null);
        Assert.Equal(["Sheet1", "Sheet3"], TabNames);
    }

    [Fact]
    public void Delete_TheLastTab_ActivatesTheNewLastTab_AndTheOnlySheetCannotBeDeleted()
    {
        _vm.AddSheet(); // Sheet2 active, last

        Assert.True(_vm.DeleteActiveSheet());

        Assert.Equal(["Sheet1"], TabNames);
        Assert.Equal(0, _vm.ActiveSheetIndex);
        Assert.False(_vm.DeleteSheetCommand.CanExecute(null));
        Assert.False(_vm.DeleteActiveSheet());
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public void UndoOfAdd_WhileTheAddedSheetIsActive_ShowsTheNewActiveSheet_AtA1()
    {
        Wb.SetCell("A1", "1");
        Go("C3");
        _vm.AddSheet();
        Go("D4");

        _vm.UndoCommand.Execute(null);

        Assert.Equal(["Sheet1"], TabNames);
        Assert.Equal(0, _vm.ActiveSheetIndex);
        Assert.Equal("A1", _vm.ActiveAddress);
        Assert.Equal("N:1", _vm.SelectionValue);

        _vm.RedoCommand.Execute(null); // the sheet is back; Sheet1 stays active
        Assert.Equal(["Sheet1", "Sheet2"], TabNames);
        Assert.Equal(0, _vm.ActiveSheetIndex);
    }

    // ---- move ----

    [Fact]
    public void MoveLeftAndRight_KeepTheSheetActive_AndAreDisabledAtTheEnds()
    {
        _vm.AddSheet();
        _vm.AddSheet();
        _vm.ActiveSheetIndex = 1; // Sheet2
        Go("C3");

        _vm.MoveSheetLeftCommand.Execute(null);
        Assert.Equal(["Sheet2", "Sheet1", "Sheet3"], TabNames);
        Assert.Equal(0, _vm.ActiveSheetIndex);
        Assert.Equal("Sheet2", _vm.ActiveSheetName);
        Assert.Equal("C3", _vm.ActiveAddress);
        Assert.False(_vm.MoveSheetLeftCommand.CanExecute(null));
        Assert.True(_vm.MoveSheetRightCommand.CanExecute(null));

        _vm.MoveSheetRightCommand.Execute(null);
        _vm.MoveSheetRightCommand.Execute(null);
        Assert.Equal(["Sheet1", "Sheet3", "Sheet2"], TabNames);
        Assert.Equal(2, _vm.ActiveSheetIndex);
        Assert.False(_vm.MoveSheetRightCommand.CanExecute(null));
        Assert.True(_vm.MoveSheetLeftCommand.CanExecute(null));

        _vm.UndoCommand.Execute(null);
        Assert.Equal(["Sheet1", "Sheet2", "Sheet3"], TabNames);
        Assert.Equal(1, _vm.ActiveSheetIndex);
    }

    [Fact]
    public void Drag_MovesTheDraggedSheet_AsOneUndoStep()
    {
        _vm.AddSheet();
        _vm.AddSheet(); // Sheet3 active
        Wb.MarkSaved();

        Assert.True(_vm.MoveSheet(0, 2));

        Assert.Equal(["Sheet2", "Sheet3", "Sheet1"], TabNames);
        Assert.Equal("Sheet3", _vm.ActiveSheetName);
        Assert.Equal(1, _vm.ActiveSheetIndex);
        Assert.True(_vm.IsDirty);
        Assert.False(_vm.MoveSheet(1, 1)); // dropped on itself
        Assert.False(_vm.MoveSheet(0, 3));

        _vm.UndoCommand.Execute(null);
        Assert.Equal(["Sheet1", "Sheet2", "Sheet3"], TabNames);
        Assert.False(_vm.IsDirty); // one step
    }

    // ---- the name box (R88) ----

    [Theory]
    [InlineData("Data!B2")]
    [InlineData("data!b2")]
    [InlineData("  'Data'!$B$2 ")]
    public void NameBox_QualifiedAddress_ActivatesTheSheet_AndSelectsTheCell(string typed)
    {
        Wb.AddSheet("Data");
        Wb.SetCell("Data!B2", "42");
        Wb.MarkSaved();

        _vm.NameBoxText = typed;
        Assert.True(_vm.NavigateToNameBox());

        Assert.Equal("Data", Wb.ActiveSheet);
        Assert.Equal(1, _vm.ActiveSheetIndex);
        Assert.Equal(["Sheet1", "Data"], TabNames);
        Assert.Equal("B2", _vm.ActiveAddress);
        Assert.Equal("B2", _vm.NameBoxText); // unqualified
        Assert.Equal("N:42", _vm.SelectionValue);
        Assert.False(_vm.IsDirty);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public void NameBox_QuotedSheetNeedingQuotes_AndTheActiveSheetsOwnPrefix()
    {
        Wb.AddSheet("My Sheet");
        _vm.NameBoxText = "'my sheet'!c3";
        Assert.True(_vm.NavigateToNameBox());
        Assert.Equal("My Sheet", _vm.ActiveSheetName);
        Assert.Equal("C3", _vm.NameBoxText);

        _vm.NameBoxText = "'My Sheet'!D4"; // the active sheet: just moves
        Assert.True(_vm.NavigateToNameBox());
        Assert.Equal("D4", _vm.ActiveAddress);
        Assert.Equal("My Sheet", _vm.ActiveSheetName);
    }

    [Fact]
    public void NameBox_UnknownSheet_ShowsAMessage_AndChangesNothing()
    {
        Go("C3");
        _vm.NameBoxText = "Nope!B2";

        Assert.False(_vm.NavigateToNameBox());

        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains("Nope", error.Message);
        Assert.Equal("Sheet1", Wb.ActiveSheet);
        Assert.Equal("C3", _vm.ActiveAddress);
        Assert.Equal("C3", _vm.NameBoxText);
    }

    [Theory]
    [InlineData("My Sheet!A1")] // R82: a prefix needing quotes must be quoted
    [InlineData("Data!")]
    [InlineData("'Data!A1")]
    [InlineData("Data!A1:B2")]
    public void NameBox_MalformedQualifiedAddress_RevertsWithoutAMessage(string typed)
    {
        Wb.AddSheet("Data");
        _vm.NameBoxText = typed;

        Assert.False(_vm.NavigateToNameBox());

        Assert.Empty(_dialogs.Errors);
        Assert.Equal("A1", _vm.NameBoxText);
        Assert.Equal("Sheet1", Wb.ActiveSheet);
    }

    [Fact]
    public void NameBox_QualifiedAddress_CommitsAnOpenEditFirst()
    {
        Wb.AddSheet("Data");
        _vm.BeginEdit("x");
        _vm.NameBoxText = "Data!C1";

        Assert.True(_vm.NavigateToNameBox());

        Assert.Equal("x", Wb.GetInput("Sheet1!A1"));
        Assert.Equal("Data", Wb.ActiveSheet);
        Assert.Equal("C1", _vm.ActiveAddress);
    }

    // ---- adopting a workbook (New, Open, Import, Restore) ----

    [Fact]
    public void Open_ShowsTheFilesSheetsAndActiveSheet_AndNewResetsToOneTab()
    {
        string dir = NewTempDirectory();
        try
        {
            var wb = new Workbook();
            wb.AddSheet("Data");
            wb.AddSheet("Notes");
            wb.SetCell("Notes!A1", "n");
            wb.ActiveSheet = "Notes";
            string path = Path.Combine(dir, "three.gridline");
            GridlineFile.SaveToPath(wb, path);
            _dialogs.OpenPath = path;

            Assert.True(_vm.Open());

            Assert.Equal(["Sheet1", "Data", "Notes"], TabNames);
            Assert.Equal(2, _vm.ActiveSheetIndex);
            Assert.Equal("T:n", _vm.SelectionValue);
            Assert.False(_vm.IsDirty);
            Assert.Equal("Gridline - three.gridline", _vm.Title);

            _vm.ActiveSheetIndex = 0;
            Assert.False(_vm.IsDirty); // R80: activation does not dirty
            Assert.Equal("Gridline - three.gridline", _vm.Title);

            Assert.True(_vm.New());
            Assert.Equal(["Sheet1"], TabNames);
            Assert.Equal(0, _vm.ActiveSheetIndex);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- the internal copy across sheets (R47, D-59) ----

    [Fact]
    public void InternalPaste_AfterSwitchingSheets_CopiesFromTheCopiedSheet()
    {
        Wb.ApplyBatch([new("A1", "1"), new("A2", "=A1*2")]);
        Select("A1", "A2");
        Assert.True(_vm.Copy());
        Assert.Equal("Sheet1", _vm.CopiedSheet);

        _vm.AddSheet();
        Go("B1");
        Assert.True(_vm.Paste());

        Assert.Equal("1", Wb.GetInput("Sheet2!B1"));
        Assert.Equal("=B1*2", Wb.GetInput("Sheet2!B2")); // internal: the formula, shifted (§9.4)
        Assert.Equal("N:1", _vm.SelectionValue);
        Assert.Equal("=A1*2", Wb.GetInput("Sheet1!A2"));
    }

    [Fact]
    public void InternalCopy_IsForgotten_OnlyByChangesOnTheCopiedSheet_OrItsDeletion()
    {
        Wb.ApplyBatch([new("A1", "1"), new("A2", "2")]);
        Select("A1", "A2");
        _vm.Copy();
        _vm.AddSheet();

        Wb.SetCell("A1", "on sheet 2"); // the same address, another sheet
        Assert.True(_vm.IsInternalPaste(_clipboard.Text));

        _dialogs.SheetNameAnswer = "Renamed";
        _vm.ActiveSheetIndex = 0;
        _vm.RenameActiveSheet(); // the copied sheet, renamed: still the same range
        Assert.Equal("Renamed", _vm.CopiedSheet);
        Assert.True(_vm.IsInternalPaste(_clipboard.Text));

        Wb.SetCell("Renamed!A2", "changed");
        Assert.False(_vm.IsInternalPaste(_clipboard.Text));

        Select("A1", "A2");
        _vm.Copy();
        _vm.DeleteActiveSheet();
        Assert.False(_vm.IsInternalPaste(_clipboard.Text));
        Assert.Null(_vm.CopiedSheet);
    }

    [Fact]
    public void Commands_AreUnavailable_UntilStartupHasCompleted()
    {
        var recovery = new StubRecovery();
        var vm = new MainViewModel(_dialogs, _clipboard, recovery);
        Assert.True(vm.BeginStartup(null));
        Assert.False(vm.AddSheetCommand.CanExecute(null));
        Assert.False(vm.RenameSheetCommand.CanExecute(null));
        Assert.False(vm.AddSheet());
        Assert.Equal(["Sheet1"], vm.SheetTabs.Select(t => t.Name));
        vm.CompleteStartup();
        Assert.True(vm.AddSheetCommand.CanExecute(null));
    }

    /// <summary>A recovery store that reports one pending file, so startup waits for <c>CompleteStartup</c>.</summary>
    private sealed class StubRecovery : IRecoveryStore
    {
        public IReadOnlyList<RecoveryFile> List() => [new RecoveryFile(Guid.NewGuid(), "x", DateTime.UtcNow)];
        public string PathFor(Guid sessionId) => "x";
        public void Write(Workbook workbook, Guid sessionId) { }
        public bool TryDelete(Guid sessionId) => true;
        public bool TryClaim(Guid sessionId) => true;
        public void Release(Guid sessionId) { }
        public bool IsInUse(Guid sessionId) => false;
    }
}
