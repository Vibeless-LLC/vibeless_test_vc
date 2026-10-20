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

    [Theory]
    [InlineData(1, 0, true)]                // small selections are always probed (no used-cell scan per arrow key)
    [InlineData(4096, 100000, true)]
    [InlineData(4096, 0, true)]
    [InlineData(5000, 100000, true)]        // area <= used: probe the selection
    [InlineData(5000, 4999, false)]         // area > used: filter the used cells
    [InlineData(70_200_000, 3, false)]      // whole sheet, sparse
    [InlineData(70_200_000, 100000, false)] // whole sheet, 100k cells
    public void Strategy_IsTheCheaperOfAreaAndUsedCells(long area, int used, bool probes) =>
        Assert.Equal(probes, SelectionSummary.ProbesSelection(area, used));

    [Fact]
    public void LargeSelectionOnADenseSheet_ProbesAndSumsCorrectly()
    {
        var cells = new List<KeyValuePair<string, string>>();
        for (int row = 1; row <= 1000; row++)
            for (int column = 1; column <= 10; column++)
                cells.Add(new(new CellAddress(column, row).ToString(), (row * column).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var wb = Workbook.FromCells(cells); // 10,000 used cells
        var selection = CellRange.Parse("A1:E1000"); // 5,000 cells: over the probe limit, under the used count
        Assert.True(SelectionSummary.ProbesSelection(selection.CellCount, 10000));
        Assert.Equal("7507500", SelectionSummary.FormatSum(wb, selection)); // (1+...+5) * (1+...+1000)
        Assert.Equal("27527500", Sum(wb, "A1:ZZ1000")); // 702,000 cells > 10,000 used: filtered; 55 * 500500
    }

    [Fact]
    public void LargeSelectionThatProbes_DoesNotFetchTheSortedUsedCells()
    {
        // Final review: the strategy is chosen from the O(1) used-cell count, so a probed 5,000-cell selection on a
        // 100,000-cell sheet never builds GetUsedAddresses' 100,000 sorted address strings (several MB).
        var cells = new List<KeyValuePair<string, string>>(100_000);
        for (int row = 1; row <= 10_000; row++)
            for (int column = 1; column <= 10; column++)
                cells.Add(new(new CellAddress(column, row).ToString(), "1"));
        var wb = Workbook.FromCells(cells);
        var selection = CellRange.Parse("A1:E1000"); // 5,000 cells, all used: probed
        Assert.True(selection.CellCount > SelectionSummary.ProbeLimit);
        Assert.True(SelectionSummary.ProbesSelection(selection.CellCount, 100_000));
        SelectionSummary.UsedCellsIn(wb, selection); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        var used = SelectionSummary.UsedCellsIn(wb, selection);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(5000, used.Count);
        Assert.Equal("A1", used[0]);
        Assert.Equal("E1000", used[^1]);
        long fetchCost = 100_000L * 24; // at least one string object per used cell if the sorted list were built
        Assert.True(allocated < fetchCost / 4, $"allocated {allocated} bytes");
        Assert.Equal(100_000, wb.GetUsedAddresses().Count);
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
