using System.Diagnostics;
using Gridline.Core;
using Xunit.Abstractions;

namespace Gridline.IO.Tests;

/// <summary>
/// Internal sanity gate for §9.5 structure edits (task 3 brief; not a §12 budget): InsertRows(1,1) and DeleteRows(1,1)
/// on the R32 P-1 fixture (100,000 cells: A1:A50000 numbers, A50001:A100000 "=A{r-50000}*2") each complete within 2 s
/// in Release, undo included. Same conventions as <see cref="CopyPasteScaleGates"/>: the non-parallel "Performance"
/// collection, Release-only, public Workbook API, fixtures built outside the timed region, median of 3 runs.
/// The P-1 fixture fills every row of column A, so InsertRows(1,1) on it must throw InvalidOperationException (§9.5)
/// and change nothing; that rejection is timed too, and the insert itself is timed on the fixture minus its last row
/// (99,999 cells, every one of them moving and every formula rewritten). See DECISIONS D-43.
/// </summary>
[Collection("Performance")]
[Trait("Category", "Performance")]
public class StructureEditScaleGates(ITestOutputHelper output)
{
#if DEBUG
    private const string? ReleaseOnly = "§12 budgets are defined for Release builds";
#else
    private const string? ReleaseOnly = null;
#endif

    private const int Runs = 3;
    private const double BudgetMs = 2000;

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private static double Time(Action action)
    {
        long t0 = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private static void WarmUp()
    {
        var warm = Workbook.FromCells(PerformanceGates.HalfConstantsHalfFormulas().Take(2_000));
        warm.InsertRows(1, 1);
        warm.Undo();
        warm.DeleteRows(1, 1);
        warm.Undo();
    }

    [Fact(Skip = ReleaseOnly)]
    public void DeleteRows1_OnP1Fixture_WithUndo_Within2s()
    {
        WarmUp();
        var wb = Workbook.FromCells(PerformanceGates.HalfConstantsHalfFormulas());
        var times = new List<double>();
        for (int i = 0; i < Runs; i++)
        {
            double edit = Time(() => wb.DeleteRows(1, 1));
            Assert.Equal(99_999, wb.GetUsedAddresses().Count);
            Assert.Equal("=#REF!*2", wb.GetInput("A50000"));
            Assert.Equal("=A1*2", wb.GetInput("A50001"));
            Assert.Equal(CellValue.FromNumber(4), wb.GetValue("A50001"));
            Assert.Equal(50_000, wb.LastRecalc.EvaluatedFormulaCount);
            double undo = Time(() => Assert.True(wb.Undo()));
            Assert.Equal("=A1*2", wb.GetInput("A50001"));
            Assert.Equal(CellValue.FromNumber(2), wb.GetValue("A50001"));
            output.WriteLine($"DeleteRows(1,1) on 100,000 cells: {edit:F1} ms; undo {undo:F1} ms");
            times.Add(edit + undo);
        }
        double median = Median(times);
        output.WriteLine($"DeleteRows(1,1) + undo median {median:F1} ms");
        Assert.True(median <= BudgetMs, $"DeleteRows(1,1) + undo median {median:F1} ms > {BudgetMs} ms");
    }

    [Fact(Skip = ReleaseOnly)]
    public void InsertRows1_OnP1Fixture_WithUndo_Within2s()
    {
        WarmUp();
        var full = Workbook.FromCells(PerformanceGates.HalfConstantsHalfFormulas());
        double rejected = Time(() => Assert.Throws<InvalidOperationException>(() => full.InsertRows(1, 1))); // A100000 has input
        Assert.False(full.CanUndo);
        Assert.Equal("=A50000*2", full.GetInput("A100000"));
        output.WriteLine($"InsertRows(1,1) on the full P-1 fixture rejected in {rejected:F1} ms");
        Assert.True(rejected <= BudgetMs, $"rejected InsertRows(1,1) {rejected:F1} ms > {BudgetMs} ms");

        var wb = Workbook.FromCells(PerformanceGates.HalfConstantsHalfFormulas().Take(99_999)); // room for one row
        var times = new List<double>();
        for (int i = 0; i < Runs; i++)
        {
            double edit = Time(() => wb.InsertRows(1, 1));
            Assert.Equal("", wb.GetInput("A1"));
            Assert.Equal("=A2*2", wb.GetInput("A50002"));
            Assert.Equal(CellValue.FromNumber(2), wb.GetValue("A50002"));
            Assert.Equal(49_999, wb.LastRecalc.EvaluatedFormulaCount);
            double undo = Time(() => Assert.True(wb.Undo()));
            Assert.Equal("=A1*2", wb.GetInput("A50001"));
            output.WriteLine($"InsertRows(1,1) on 99,999 cells: {edit:F1} ms; undo {undo:F1} ms");
            times.Add(edit + undo);
        }
        double median = Median(times);
        output.WriteLine($"InsertRows(1,1) + undo median {median:F1} ms");
        Assert.True(median <= BudgetMs, $"InsertRows(1,1) + undo median {median:F1} ms > {BudgetMs} ms");
    }
}
