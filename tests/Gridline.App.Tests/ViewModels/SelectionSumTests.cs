using System.Diagnostics;
using Gridline.App.ViewModels;
using Gridline.Core;
using Gridline.Core.Addressing;
using static Gridline.App.Tests.TestSupport;

namespace Gridline.App.Tests.ViewModels;

/// <summary>§11.7 SelectionSum, ruling R43: sum of Number values in the selection, ToString("R", invariant), "0" when
/// none, -0 as 0; only used cells inside the selection are visited.</summary>
public class SelectionSumTests
{
    private static string Sum(Workbook wb, string range) => SelectionSummary.FormatSum(wb, CellRange.Parse(range));

    [Fact]
    public void NoNumbers_IsZero()
    {
        var wb = Workbook.FromCells([new("A1", "text"), new("A2", "TRUE"), new("A3", "=1/0")]);
        Assert.Equal("0", Sum(wb, "A1:A3"));
        Assert.Equal("0", Sum(new Workbook(), "A1:B2"));
    }

    [Fact]
    public void SumsOnlyNumbers_IncludingFormulaResults()
    {
        var wb = Workbook.FromCells([
            new("A1", "1"), new("A2", "2.5"), new("A3", "hello"), new("A4", "TRUE"), new("A5", "=A1+A2"), new("A6", "=1/0"),
            new("B1", "100"), // outside the selection
        ]);
        Assert.Equal("7", Sum(wb, "A1:A6"));
    }

    [Fact]
    public void UsesRoundTripFormat()
    {
        var wb = Workbook.FromCells([new("A1", "0.1"), new("A2", "0.2")]);
        Assert.Equal("0.30000000000000004", Sum(wb, "A1:A2"));
        var big = Workbook.FromCells([new("A1", "1e20"), new("A2", "1")]);
        Assert.Equal("1E+20", Sum(big, "A1:A2"));
    }

    [Fact]
    public void CancellingValues_PrintZero_AndNegativeZeroPrintsZero()
    {
        var wb = Workbook.FromCells([new("A1", "-1.5"), new("A2", "1.5")]);
        Assert.Equal("0", Sum(wb, "A1:A2"));
        Assert.Equal("0", SelectionSummary.Format(-0.0));
        Assert.Equal("-2", SelectionSummary.Format(-2));
    }

    [Fact]
    public void ProbeAndUsedCellStrategies_Agree()
    {
        // Same cells, one selection under the probe limit and one over it (row-major in both).
        var cells = new Dictionary<string, string>();
        var rnd = new Random(7);
        for (int i = 0; i < 300; i++)
            cells[new CellAddress(rnd.Next(1, 40), rnd.Next(1, 60)).ToString()] = (rnd.NextDouble() * 1e6 - 5e5).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var wb = Workbook.FromCells(cells);
        var small = CellRange.Parse("A1:AN60"); // 40 x 60 = 2400 cells, probed
        Assert.True(small.CellCount <= SelectionSummary.ProbeLimit);
        var large = CellRange.Parse("A1:ZZ60"); // over the limit, filtered from used cells
        Assert.True(large.CellCount > SelectionSummary.ProbeLimit);
        Assert.Equal(SelectionSummary.FormatSum(wb, small), SelectionSummary.FormatSum(wb, large));
    }

    [Fact]
    public void WholeSheetSelection_VisitsOnlyUsedCells()
    {
        var wb = Workbook.FromCells([new("A1", "1"), new("ZZ100000", "2"), new("M500", "3")]);
        var sw = Stopwatch.StartNew();
        Assert.Equal("6", Sum(wb, "A1:ZZ100000")); // 70.2 million cells: probing each would take far longer
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ViewModel_UpdatesSumWithSelectionAndEdits()
    {
        var vm = NewViewModel();
        vm.Workbook.ApplyBatch([new("A1", "1"), new("A2", "2"), new("B1", "4")]);
        Assert.Equal("1", vm.SelectionSum); // A1 only
        vm.MoveActiveCell(0, 1, extend: true);
        Assert.Equal("3", vm.SelectionSum); // A1:A2
        vm.MoveActiveCell(1, 0, extend: true);
        Assert.Equal("7", vm.SelectionSum); // A1:B2
        vm.Workbook.SetCell("B2", "0.5");
        Assert.Equal("7.5", vm.SelectionSum);
    }
}
