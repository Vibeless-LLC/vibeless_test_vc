using Gridline.Core.Evaluation;
using Gridline.Core.Functions;

namespace Gridline.Core.Tests;

// Requirement IDs: §7.4 (ROW()/COLUMN() own cell, CHOOSE static dependencies), §9.4 (CopyPaste), §9.5 (insert/delete), §6.1.
public class WorkbookLookupTests
{
    private static CellValue N(double d) => CellValue.FromNumber(d);

    private static Workbook Book(params (string Address, string Input)[] cells) =>
        Workbook.FromCells(cells.Select(c => new KeyValuePair<string, string>(c.Address, c.Input)));

    [Fact]
    public void RowColumn_FollowTheCell_AfterCopyPaste()
    {
        var wb = Book(("A1", "=ROW()"), ("B1", "=COLUMN()"), ("C1", "=ROW(A1)+COLUMN(A1)"));
        wb.CopyPaste("A1:C1", "D5");
        Assert.Equal(N(5), wb.GetValue("D5"));
        Assert.Equal(N(5), wb.GetValue("E5"));      // COLUMN() of E5
        Assert.Equal("=ROW()", wb.GetInput("D5"));  // text unchanged: no references to shift
        Assert.Equal("=COLUMN()", wb.GetInput("E5"));
        Assert.Equal(N(1 + 1), wb.GetValue("C1"));
        Assert.Equal(N(5 + 4), wb.GetValue("F5"));  // ROW(D5) + COLUMN(D5): the reference shifted with the paste
        Assert.True(wb.Undo());
        Assert.Equal(CellValue.Blank, wb.GetValue("D5"));
        Assert.Equal(N(1), wb.GetValue("A1"));
    }

    [Fact]
    public void RowColumn_Recomputed_AfterInsertAndDelete()
    {
        var wb = Book(("A1", "x"), ("A3", "=ROW()"), ("C3", "=COLUMN()"));
        wb.InsertRows(1, 2);
        Assert.Equal(N(5), wb.GetValue("A5"));
        wb.InsertColumns(1, 1);
        Assert.Equal(N(5), wb.GetValue("B5"));
        Assert.Equal(N(4), wb.GetValue("D5"));
        wb.DeleteRows(1, 2);
        Assert.Equal(N(3), wb.GetValue("B3"));
    }

    [Fact]
    public void Choose_UnchosenBranchesAreStaticDependencies()
    {
        var wb = Book(("A1", "1"), ("B1", "10"), ("B2", "20"), ("C1", "=CHOOSE(A1,B1,B2)"));
        Assert.Equal(N(10), wb.GetValue("C1"));
        wb.SetCell("B2", "99");                      // not chosen, still a dependency
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("A1", "2");
        Assert.Equal(N(99), wb.GetValue("C1"));
        wb.SetCell("B1", "=1/0");                    // unchosen error does not surface
        Assert.Equal(N(99), wb.GetValue("C1"));
        wb.SetCell("A1", "1");
        Assert.Equal(CellValue.FromError(ErrorCodes.Div0), wb.GetValue("C1"));
    }

    [Fact]
    public void Lookup_RecalculatesWhenTableChanges()
    {
        var wb = Book(("A1", "1"), ("A2", "2"), ("B1", "a"), ("B2", "b"), ("D1", "=VLOOKUP(2,A1:B2,2,FALSE)"),
            ("D2", "=MATCH(2,A1:A2,0)"), ("D3", "=INDEX(B1:B2,D2)"));
        Assert.Equal(CellValue.FromText("b"), wb.GetValue("D1"));
        Assert.Equal(CellValue.FromText("b"), wb.GetValue("D3"));
        wb.SetCell("A2", "5");
        Assert.Equal(CellValue.FromError(ErrorCodes.NA), wb.GetValue("D1"));
        Assert.Equal(CellValue.FromError(ErrorCodes.NA), wb.GetValue("D3"));
    }

    [Fact]
    public void LookupRanges_AdjustOnInsertRows()
    {
        var wb = Book(("A1", "1"), ("A2", "2"), ("B1", "a"), ("B2", "b"), ("D1", "=VLOOKUP(2,A1:B2,2,FALSE)"));
        wb.InsertRows(1, 1);
        Assert.Equal("=VLOOKUP(2,A2:B3,2,FALSE)", wb.GetInput("D2"));
        Assert.Equal(CellValue.FromText("b"), wb.GetValue("D2"));
    }
}
