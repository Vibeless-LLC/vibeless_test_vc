using Gridline.App.Controls;
using Gridline.App.ViewModels;
using Gridline.Core;
using Gridline.Core.Addressing;
using static Gridline.App.Tests.TestSupport;

namespace Gridline.App.Tests.ViewModels;

/// <summary>Header selection and the header context menu's Insert and Delete (§11.3, §11.5, §9.5, R75, D-49): whole-row
/// and whole-column selection, Shift extension, the right-click rule, counts and the first index, one undo step each, a
/// refused edit's message, the derived texts afterwards, and SelectionSum on whole-row and whole-column selections.</summary>
public sealed class StructureCommandTests
{
    private readonly FakeDialogService _dialogs = new();
    private readonly MainViewModel _vm;

    public StructureCommandTests() => _vm = NewViewModel(_dialogs);

    private static CellAddress At(string address) => CellAddress.Parse(address);

    private void Load(params (string Address, string Input)[] cells)
    {
        foreach (var (address, input) in cells) _vm.Workbook.SetCell(address, input);
    }

    // ---- header selection (R75) ----

    [Fact]
    public void RowHeader_SelectsTheWholeRow_AtoZZ_WithTheActiveCellInTheGivenColumn()
    {
        _vm.SelectHeaders(HeaderAxis.Rows, 3, 2, extend: false);

        Assert.Equal(CellRange.Parse("A3:ZZ3"), _vm.Selection);
        Assert.Equal("B3", _vm.ActiveAddress);
        Assert.Equal("B3", _vm.NameBoxText);
        Assert.Equal(HeaderAxis.Rows, _vm.HeaderSelectionAxis);
        Assert.True(_vm.HasHeaderSelection);
        Assert.True(_vm.InsertRowsOrColumnsCommand.CanExecute(null));
    }

    [Fact]
    public void ColumnHeader_SelectsTheWholeColumn_1to100000()
    {
        _vm.SelectHeaders(HeaderAxis.Columns, 4, 7, extend: false);

        Assert.Equal(CellRange.Parse("D1:D100000"), _vm.Selection);
        Assert.Equal("D7", _vm.ActiveAddress);
        Assert.Equal(HeaderAxis.Columns, _vm.HeaderSelectionAxis);
    }

    [Fact]
    public void ShiftClick_ExtendsTheWholeRowsOrColumns_InEitherDirection()
    {
        _vm.SelectHeaders(HeaderAxis.Rows, 5, 1, extend: false);
        _vm.SelectHeaders(HeaderAxis.Rows, 2, 1, extend: true);
        Assert.Equal(CellRange.Parse("A2:ZZ5"), _vm.Selection);

        _vm.SelectHeaders(HeaderAxis.Columns, 3, 1, extend: false);
        _vm.SelectHeaders(HeaderAxis.Columns, 6, 1, extend: true);
        Assert.Equal(CellRange.Parse("C1:F100000"), _vm.Selection);
    }

    [Fact]
    public void ShiftArrows_KeepAWholeRowSelectionWhole_APlainMoveOrClickMakesItACellSelection()
    {
        _vm.SelectHeaders(HeaderAxis.Rows, 2, 1, extend: false);
        _vm.MoveActiveCell(0, 2, extend: true);
        Assert.Equal(CellRange.Parse("A2:ZZ4"), _vm.Selection);

        _vm.MoveActiveCell(1, 0, extend: false);
        Assert.Null(_vm.HeaderSelectionAxis);
        Assert.Equal(CellRange.Parse("B4:B4"), _vm.Selection);
        Assert.False(_vm.InsertRowsOrColumnsCommand.CanExecute(null));
        Assert.False(_vm.DeleteSelected());

        _vm.SelectHeaders(HeaderAxis.Columns, 2, 1, extend: false);
        _vm.SelectCell(At("C3"), extend: false);
        Assert.Null(_vm.HeaderSelectionAxis);
    }

    [Fact]
    public void ShiftClickOnAHeader_FromACellSelection_ExtendsFromTheAnchor()
    {
        _vm.SelectCell(At("B2"), extend: false);
        _vm.SelectHeaders(HeaderAxis.Rows, 4, 1, extend: true);
        Assert.Equal(CellRange.Parse("A2:ZZ4"), _vm.Selection);
    }

    [Fact]
    public void RightClick_OutsideTheWholeSelection_SelectsThatHeader_InsideKeepsTheSelection()
    {
        _vm.SelectHeaders(HeaderAxis.Rows, 2, 1, extend: false);
        _vm.SelectHeaders(HeaderAxis.Rows, 4, 1, extend: true);

        _vm.PrepareHeaderMenu(HeaderAxis.Rows, 3, 1); // inside rows 2..4
        Assert.Equal(CellRange.Parse("A2:ZZ4"), _vm.Selection);

        _vm.PrepareHeaderMenu(HeaderAxis.Rows, 7, 1); // outside
        Assert.Equal(CellRange.Parse("A7:ZZ7"), _vm.Selection);

        _vm.PrepareHeaderMenu(HeaderAxis.Columns, 1, 1); // the other axis is always outside a row selection
        Assert.Equal(CellRange.Parse("A1:A100000"), _vm.Selection);

        _vm.SelectCell(At("B2"), extend: false); // a cell selection: any header is outside
        _vm.PrepareHeaderMenu(HeaderAxis.Columns, 2, 1);
        Assert.Equal(CellRange.Parse("B1:B100000"), _vm.Selection);
    }

    // ---- Insert and Delete ----

    [Fact]
    public void InsertRows_InsertsTheSelectedCount_AtTheFirstSelectedRow_AsOneUndoStep()
    {
        Load(("A1", "1"), ("A2", "2"), ("A3", "3"), ("B5", "=SUM(A1:A3)"));
        _vm.SelectHeaders(HeaderAxis.Rows, 3, 1, extend: false);
        _vm.SelectHeaders(HeaderAxis.Rows, 2, 1, extend: true); // rows 2..3

        _vm.InsertRowsOrColumnsCommand.Execute(null);

        Assert.Equal("1", _vm.Workbook.GetInput("A1"));
        Assert.Equal("", _vm.Workbook.GetInput("A2"));
        Assert.Equal("", _vm.Workbook.GetInput("A3"));
        Assert.Equal("2", _vm.Workbook.GetInput("A4"));
        Assert.Equal("3", _vm.Workbook.GetInput("A5"));
        Assert.Equal("=SUM(A1:A5)", _vm.Workbook.GetInput("B7"));
        Assert.Equal(CellRange.Parse("A2:ZZ3"), _vm.Selection); // the selection stays where it was
        Assert.Equal("Gridline - Untitled *", _vm.Title);

        _vm.UndoCommand.Execute(null); // Ctrl+Z
        Assert.Equal("2", _vm.Workbook.GetInput("A2"));
        Assert.Equal("=SUM(A1:A3)", _vm.Workbook.GetInput("B5"));
        _vm.UndoCommand.Execute(null);
        _vm.UndoCommand.Execute(null);
        _vm.UndoCommand.Execute(null);
        _vm.UndoCommand.Execute(null); // the four SetCells
        Assert.False(_vm.IsDirty);
        Assert.Equal("Gridline - Untitled", _vm.Title);
    }

    [Fact]
    public void DeleteRows_DeletesTheSelectedRows()
    {
        Load(("A1", "1"), ("A2", "2"), ("A3", "3"), ("A4", "4"));
        _vm.SelectHeaders(HeaderAxis.Rows, 2, 1, extend: false);
        _vm.SelectHeaders(HeaderAxis.Rows, 3, 1, extend: true);

        Assert.True(_vm.DeleteSelected());

        Assert.Equal("1", _vm.Workbook.GetInput("A1"));
        Assert.Equal("4", _vm.Workbook.GetInput("A2"));
        Assert.Equal("", _vm.Workbook.GetInput("A3"));
        _vm.Workbook.Undo();
        Assert.Equal("3", _vm.Workbook.GetInput("A3"));
    }

    [Fact]
    public void InsertAndDeleteColumns_UseTheSelectedColumns()
    {
        Load(("A1", "a"), ("B1", "b"), ("C1", "c"), ("D1", "=C1"));
        _vm.SelectHeaders(HeaderAxis.Columns, 2, 1, extend: false);
        _vm.SelectHeaders(HeaderAxis.Columns, 3, 1, extend: true); // B..C

        Assert.True(_vm.InsertSelected());
        Assert.Equal("b", _vm.Workbook.GetInput("D1"));
        Assert.Equal("=E1", _vm.Workbook.GetInput("F1"));

        Assert.True(_vm.DeleteSelected()); // B..C again: the inserted columns
        Assert.Equal("b", _vm.Workbook.GetInput("B1"));
        Assert.Equal("=C1", _vm.Workbook.GetInput("D1"));

        _vm.SelectHeaders(HeaderAxis.Columns, 1, 1, extend: false);
        _vm.DeleteRowsOrColumnsCommand.Execute(null);
        Assert.Equal("b", _vm.Workbook.GetInput("A1"));
        _vm.UndoCommand.Execute(null);
        Assert.Equal("a", _vm.Workbook.GetInput("A1"));
    }

    [Fact]
    public void Refused_ShowsAMessage_AndChangesNothing()
    {
        Load(("A100000", "last"));
        _vm.SelectHeaders(HeaderAxis.Rows, 1, 1, extend: false);
        bool canUndoBefore = _vm.Workbook.CanUndo;

        Assert.False(_vm.InsertSelected()); // A100000 would move past the last row (InvalidOperationException)

        var error = Assert.Single(_dialogs.Errors);
        Assert.Contains("nothing was changed", error.Message);
        Assert.Equal("last", _vm.Workbook.GetInput("A100000"));
        Assert.Equal(canUndoBefore, _vm.Workbook.CanUndo);
        _vm.Workbook.Undo(); // the only step is the SetCell
        Assert.False(_vm.Workbook.CanUndo);
    }

    [Fact]
    public void WholeSheetRowSelection_DeletesEverything_AndInsertIsRefusedWhenCellsWouldFallOff()
    {
        Load(("C3", "x"));
        _vm.SelectHeaders(HeaderAxis.Rows, 1, 1, extend: false);
        _vm.SelectHeaders(HeaderAxis.Rows, GridBounds.MaxRows, 1, extend: true);

        Assert.False(_vm.InsertSelected());
        Assert.Single(_dialogs.Errors);
        Assert.True(_vm.DeleteSelected());
        Assert.Empty(_vm.Workbook.GetUsedAddresses());
    }

    [Fact]
    public void Insert_CommitsAnOpenEditFirst()
    {
        _vm.SelectHeaders(HeaderAxis.Rows, 1, 1, extend: false);
        _vm.BeginEdit("typed"); // A1

        Assert.True(_vm.InsertSelected());

        Assert.False(_vm.IsEditing);
        Assert.Equal("typed", _vm.Workbook.GetInput("A2"));
        Assert.Equal("", _vm.Workbook.GetInput("A1"));
        _vm.Workbook.Undo();
        Assert.Equal("typed", _vm.Workbook.GetInput("A1")); // the edit and the insert are separate steps
    }

    [Fact]
    public void AfterAnEdit_TheBarsAndSum_FollowTheMovedCells()
    {
        Load(("A1", "5"), ("A2", "7"));
        _vm.SelectHeaders(HeaderAxis.Rows, 1, 1, extend: false);
        Assert.Equal("N:5", _vm.SelectionValue);
        Assert.Equal("5", _vm.FormulaBarText);
        Assert.Equal("5", _vm.SelectionSum);
        int contentChanges = 0;
        _vm.ContentChanged += (_, _) => contentChanges++;

        Assert.True(_vm.InsertSelected()); // row 1 is empty now; 5 and 7 are in A2 and A3

        Assert.Equal("A1", _vm.ActiveAddress);
        Assert.Equal("BLANK", _vm.SelectionValue);
        Assert.Equal("", _vm.FormulaBarText);
        Assert.Equal("0", _vm.SelectionSum);
        Assert.True(contentChanges > 0); // the grid repaints
        Assert.True(_vm.IsDirty);

        _vm.UndoCommand.Execute(null);
        Assert.Equal("N:5", _vm.SelectionValue);
        Assert.Equal("5", _vm.SelectionSum);
    }

    [Fact]
    public void ANamesOnlyEdit_RaisesNoCellsChanged_ButRefreshesTheTitleAndUndo()
    {
        // I-1 (§11.1, §9.3): cells only in row 1 and N=$A$100; inserting at row 50 moves no input and rewrites no
        // formula, so Core raises no CellsChanged (§9.2, R63, R64), yet N becomes $A$101 and the step is undoable.
        string dir = NewTempDirectory();
        try
        {
            string path = Path.Combine(dir, "named.gridline");
            var source = Workbook.FromCells([new("A1", "1"), new("B1", "x")]);
            source.DefineName("N", "$A$100");
            Gridline.IO.GridlineFile.SaveToPath(source, path);
            Assert.True(_vm.OpenAtStartup(path));
            Assert.Equal("Gridline - named.gridline", _vm.Title);
            Assert.False(_vm.UndoCommand.CanExecute(null));
            var changed = new List<string?>();
            _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            int undoCanExecuteChanged = 0, cellsChanged = 0;
            _vm.UndoCommand.CanExecuteChanged += (_, _) => undoCanExecuteChanged++;
            _vm.Workbook.CellsChanged += (_, _) => cellsChanged++;
            _vm.SelectHeaders(HeaderAxis.Rows, 50, 1, extend: false);
            changed.Clear();

            Assert.True(_vm.InsertSelected());

            Assert.Equal(0, cellsChanged);
            Assert.Equal("$A$101", Assert.Single(_vm.Workbook.GetNames()).Value);
            Assert.True(_vm.IsDirty);
            Assert.Equal("Gridline - named.gridline *", _vm.Title);
            Assert.Contains(nameof(MainViewModel.Title), changed);
            Assert.Contains(nameof(MainViewModel.IsDirty), changed);
            Assert.True(undoCanExecuteChanged > 0);
            Assert.True(_vm.UndoCommand.CanExecute(null));

            _vm.UndoCommand.Execute(null);
            Assert.Equal("$A$100", Assert.Single(_vm.Workbook.GetNames()).Value);
            Assert.Equal("Gridline - named.gridline", _vm.Title);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AStructureEdit_ForgetsTheInternalCopy()
    {
        var clipboard = new FakeClipboardService();
        var vm = new MainViewModel(_dialogs, clipboard);
        vm.Workbook.SetCell("A5", "=A4+1");
        vm.SelectCell(At("A5"), extend: false);
        Assert.True(vm.Copy());
        vm.SelectHeaders(HeaderAxis.Rows, 1, 1, extend: false);
        Assert.True(vm.InsertSelected()); // A5 moves to A6; the copy no longer names it

        vm.SelectCell(At("C1"), extend: false);
        Assert.True(vm.Paste());

        Assert.Equal("=A5+1", vm.Workbook.GetInput("A6"));
        Assert.Equal("1", vm.Workbook.GetInput("C1")); // the copied value as external text, not a formula copy
    }

    // ---- SelectionSum on whole rows and columns ----

    [Fact]
    public void SelectionSum_OnAWholeRowAndAWholeColumn_SumsTheirNumbers()
    {
        Load(("A2", "1"), ("ZZ2", "2"), ("C2", "text"), ("B1", "100"), ("B100000", "1000"), ("B3", "=A2*10"));

        _vm.SelectHeaders(HeaderAxis.Rows, 2, 1, extend: false);
        Assert.Equal("3", _vm.SelectionSum);

        _vm.SelectHeaders(HeaderAxis.Columns, 2, 1, extend: false);
        Assert.Equal("1110", _vm.SelectionSum);

        _vm.SelectHeaders(HeaderAxis.Columns, GridBounds.MaxColumns, 1, extend: true); // B..ZZ, 70 million cells
        Assert.Equal("1112", _vm.SelectionSum);

        _vm.Workbook.SetCell("ZZ2", "5"); // an edit refreshes it
        Assert.Equal("1115", _vm.SelectionSum);
    }

    [Fact]
    public void SelectionSum_OnAWholeColumn_OfA100kCellSheet_VisitsAtMostTheUsedCells()
    {
        var cells = new List<KeyValuePair<string, string>>(100_000);
        for (int r = 1; r <= 100_000; r++) cells.Add(new("A" + r, r <= 50_000 ? "1" : "2"));
        var wb = Workbook.FromCells(cells);

        Assert.Equal(150_000, SelectionSummary.Sum(wb, CellRange.Parse("A1:A100000")));
        Assert.Equal(0, SelectionSummary.Sum(wb, CellRange.Parse("B1:B100000")));
        Assert.Equal(1, SelectionSummary.Sum(wb, CellRange.Parse("A1:ZZ1")));
        Assert.Equal(150_000, SelectionSummary.Sum(wb, CellRange.Parse("A1:ZZ100000")));
        // Whole-row and whole-column areas (702 and 100,000 cells) never exceed the 100k used cells by more than the
        // probe limit, so the strategy is O(min(area, used cells)) (R75 responsiveness; timings in DECISIONS D-49).
        Assert.True(SelectionSummary.ProbesSelection(100_000, wb.GetUsedAddresses().Count));
        Assert.False(SelectionSummary.ProbesSelection(70_200_000, 100_000));
    }
}
