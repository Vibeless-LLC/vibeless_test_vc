using Gridline.App.ViewModels;
using Gridline.Core;
using Gridline.Core.Addressing;
using static Gridline.App.Tests.TestSupport;

namespace Gridline.App.Tests.ViewModels;

/// <summary>Active cell, selection, name box, formula bar and SelectionValue (§11.3, §11.4, §11.7, §8.3).</summary>
public class SelectionTests
{
    private static CellAddress A(string s) => CellAddress.Parse(s);

    [Fact]
    public void Initial_ActiveA1_Blank()
    {
        var vm = NewViewModel();
        Assert.Equal("A1", vm.ActiveAddress);
        Assert.Equal("A1", vm.NameBoxText);
        Assert.Equal("", vm.FormulaBarText);
        Assert.Equal("BLANK", vm.SelectionValue);
        Assert.Equal("0", vm.SelectionSum);
        Assert.Equal(new CellRange(A("A1"), A("A1")), vm.Selection);
    }

    [Theory]
    [InlineData("5", "N:5")]
    [InlineData("1.5", "N:1.5")]
    [InlineData("-0", "N:0")]
    [InlineData("hello", "T:hello")]
    [InlineData("'a\tb", "T:a\\tb")]
    [InlineData("TRUE", "B:TRUE")]
    [InlineData("=1/0", "E:#DIV/0!")]
    [InlineData("=1+", "E:#PARSE!")]
    [InlineData("=0.1+0.2", "N:0.30000000000000004")]
    public void SelectionValue_IsCanonicalValueOfActiveCell(string input, string expected)
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("B2", input);
        vm.SelectCell(A("B2"), extend: false);
        Assert.Equal(expected, vm.SelectionValue);
        Assert.Equal(input, vm.FormulaBarText);
        Assert.Equal("B2", vm.NameBoxText);
    }

    [Fact]
    public void SelectionValue_FollowsRecalculation()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("A2", "=A1*2");
        vm.SelectCell(A("A2"), extend: false);
        Assert.Equal("N:0", vm.SelectionValue);
        vm.Workbook.SetCell("A1", "21");
        Assert.Equal("N:42", vm.SelectionValue);
        Assert.Equal("42", vm.SelectionSum);
    }

    [Fact]
    public void MoveActiveCell_MovesAndClampsToGrid()
    {
        var vm = NewViewModel();
        vm.MoveActiveCell(1, 0, extend: false);
        Assert.Equal("B1", vm.ActiveAddress);
        vm.MoveActiveCell(0, 1, extend: false);
        Assert.Equal("B2", vm.ActiveAddress);
        vm.MoveActiveCell(-5, -5, extend: false);
        Assert.Equal("A1", vm.ActiveAddress);
        vm.MoveActiveCell(int.MaxValue, int.MaxValue, extend: false);
        Assert.Equal("ZZ100000", vm.ActiveAddress);
        vm.MoveActiveCell(int.MinValue, 0, extend: false);
        Assert.Equal("A100000", vm.ActiveAddress);
    }

    [Fact]
    public void ShiftMove_ExtendsFromAnchor_PlainMoveCollapses()
    {
        var vm = NewViewModel();
        vm.SelectCell(A("B2"), extend: false);
        vm.MoveActiveCell(1, 0, extend: true);
        vm.MoveActiveCell(0, 2, extend: true);
        Assert.Equal("C4", vm.ActiveAddress);
        Assert.Equal(A("B2"), vm.SelectionAnchor);
        Assert.Equal(CellRange.Parse("B2:C4"), vm.Selection);
        vm.MoveActiveCell(-3, 0, extend: true); // past the anchor: the range flips
        Assert.Equal(CellRange.Parse("A2:B4"), vm.Selection);

        vm.MoveActiveCell(0, 1, extend: false);
        Assert.Equal(CellRange.Parse("A5:A5"), vm.Selection);
    }

    [Fact]
    public void SelectionChanged_RaisedOncePerChange()
    {
        var vm = NewViewModel();
        int count = 0;
        vm.SelectionChanged += (_, _) => count++;
        vm.MoveActiveCell(1, 0, extend: false);
        Assert.Equal(1, count);
        vm.MoveActiveCell(-1, 0, extend: false);
        vm.MoveActiveCell(-1, 0, extend: false); // clamped at A1: no change
        Assert.Equal(2, count);
    }

    [Fact]
    public void GetLastUsedCell_IsMaxRowAndMaxColumn()
    {
        var vm = NewViewModel();
        Assert.Equal(A("A1"), vm.GetLastUsedCell());
        vm.Workbook.SetCell("C10", "1");
        vm.Workbook.SetCell("ZZ2", "1");
        Assert.Equal(A("ZZ10"), vm.GetLastUsedCell());
    }

    [Fact]
    public void GetDisplay_ReadsTheWorkbook()
    {
        var vm = NewViewModel();
        vm.Workbook.SetCell("C2", "12.5");
        Assert.Equal("12.5", vm.GetDisplay(3, 2).Text);
        Assert.Equal("", vm.GetDisplay(1, 1).Text);
    }

    [Fact]
    public void CellsChanged_RaisesContentChanged()
    {
        var vm = NewViewModel();
        int count = 0;
        vm.ContentChanged += (_, _) => count++;
        vm.Workbook.SetCell("A1", "1");
        vm.Workbook.ApplyBatch([new("A2", "2"), new("A3", "3")]);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Adopt_SubscribesToTheNewWorkbookOnly()
    {
        var vm = NewViewModel();
        Workbook old = vm.Workbook;
        vm.Adopt(Workbook.FromCells([new("A1", "9")]), null);
        Assert.Equal("N:9", vm.SelectionValue);
        old.SetCell("A1", "1");
        Assert.Equal("Gridline - Untitled", vm.Title); // old workbook no longer drives the title
        Assert.Equal("N:9", vm.SelectionValue);
    }
}
