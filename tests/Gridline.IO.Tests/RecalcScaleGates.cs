using System.Diagnostics;
using System.Globalization;
using Gridline.Core;
using Xunit.Abstractions;

namespace Gridline.IO.Tests;

/// <summary>
/// Phase 2 scale budgets for the dependency engine (§6.1, §6.7, §12). They sit next to <see cref="PerformanceGates"/>
/// in the same non-parallel "Performance" collection, because D-30 makes IO.Tests the place where the §12 and scale wall-clock
/// budgets are asserted and the collection keeps other tests in this assembly from disturbing the timings. They use
/// only the public Workbook API. Release-only (skipped in Debug), trait Category=Performance; fixture construction is
/// outside the timed region.
/// </summary>
[Collection("Performance")]
[Trait("Category", "Performance")]
public class RecalcScaleGates(ITestOutputHelper output)
{
#if DEBUG
    private const string? ReleaseOnly = "§12 budgets are defined for Release builds";
#else
    private const string? ReleaseOnly = null;
#endif

    private const int Runs = 5;

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>A1..A{n} = 1 and B_r = SUM($A$1:A_r): n distinct ranges that all share tile 0 of their level.</summary>
    private static Workbook RunningTotals(int n)
    {
        var cells = new List<KeyValuePair<string, string>>(2 * n);
        for (int r = 1; r <= n; r++) cells.Add(new("A" + Num(r), "1"));
        for (int r = 1; r <= n; r++) cells.Add(new("B" + Num(r), "=SUM($A$1:A" + Num(r) + ")"));
        return Workbook.FromCells(cells);
    }

    /// <summary>
    /// Task 3 (§6.1): clearing all 100,000 running-total formulas through SetCell(addr, "") within 2 s, with correct
    /// results afterwards. Cleared last row first, the worst order for a linear bucket scan. The fixture's load
    /// evaluates about 5 x 10^9 range cells, so the test times one clear of one fixture (after a warm-up clear of a
    /// small fixture) rather than the median of five.
    /// </summary>
    [Fact(Skip = ReleaseOnly)]
    public void BulkClear_100kRunningTotals_Within2s()
    {
        const int n = 100_000;
        var warm = RunningTotals(2_000);
        for (int r = 2_000; r >= 1; r--) warm.SetCell("B" + Num(r), "");

        var wb = RunningTotals(n);
        Assert.Equal(CellValue.FromNumber(n), wb.GetValue("B100000"));
        Assert.Equal(CellValue.FromNumber(1), wb.GetValue("B1"));

        long t0 = Stopwatch.GetTimestamp();
        for (int r = n; r > n / 2; r--) wb.SetCell("B" + Num(r), "");
        double first = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        // Untimed mid-point check: the remaining ranges A1:A1..A1:A50000 are still indexed, the removed ones are not.
        wb.SetCell("A50000", "2");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(CellValue.FromNumber(50_001), wb.GetValue("B50000"));
        wb.SetCell("A50001", "2");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);

        t0 = Stopwatch.GetTimestamp();
        for (int r = n / 2; r >= 1; r--) wb.SetCell("B" + Num(r), "");
        double total = first + Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        output.WriteLine($"Bulk clear of 100,000 running totals: {total:F1} ms (first half {first:F1} ms)");

        Assert.Equal(n, wb.GetUsedAddresses().Count); // only column A remains
        Assert.Equal("", wb.GetInput("B1"));
        Assert.Equal(CellValue.Blank, wb.GetValue("B77777"));
        wb.SetCell("A1", "5");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount); // no range edge survived
        wb.SetCell("C1", "=SUM(A1:A100000)");
        Assert.Equal(CellValue.FromNumber(n + 4 + 1 + 1), wb.GetValue("C1"));
        wb.SetCell("A99999", "3");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(CellValue.FromNumber(n + 4 + 1 + 1 + 2), wb.GetValue("C1"));
        Assert.True(total <= 2000, $"bulk clear {total:F1} ms > 2000 ms");
    }

    /// <summary>
    /// Task 3 (§6.7, P-2): the P-2 chain edit while the workbook also holds range formulas of 55 distinct shapes
    /// (widths 1..16 by heights 1..1024, so 55 distinct index levels), 20 copies of each spread down the sheet next to
    /// the chain. The ranges never contain chain cells, so EvaluatedFormulaCount stays 99,999.
    /// </summary>
    [Fact(Skip = ReleaseOnly)]
    public void ChainEdit_100kCells_With55RangeShapes_Within250ms()
    {
        var cells = new List<KeyValuePair<string, string>>(101_200) { new("A1", "1") };
        for (int r = 2; r <= 100_000; r++) cells.Add(new("A" + Num(r), "=A" + Num(r - 1) + "+1"));
        int shapes = 0, formulas = 0;
        for (int wi = 0; wi <= 4; wi++)
        {
            int width = 1 << wi;
            for (int hj = 0; hj <= 10; hj++)
            {
                int height = 1 << hj;
                shapes++;
                for (int copy = 0; copy < 20; copy++)
                {
                    int top = 1 + copy * 4_999;
                    string range = "B" + Num(top) + ":" + ColumnName(2 + width - 1) + Num(top + height - 1);
                    formulas++;
                    cells.Add(new(ColumnName(30 + formulas / 1000) + Num(1 + formulas % 1000), "=SUM(" + range + ")"));
                }
            }
        }
        Assert.Equal(55, shapes);
        var wb = Workbook.FromCells(cells);
        Assert.Equal(99_999 + formulas, wb.LastRecalc.EvaluatedFormulaCount);

        wb.SetCell("A1", "2"); // warm-up
        var times = new List<double>();
        for (int i = 0; i < Runs; i++)
        {
            string input = Num(i + 3);
            long t0 = Stopwatch.GetTimestamp();
            wb.SetCell("A1", input);
            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        double median = Median(times);
        output.WriteLine($"Chain edit with {shapes} range shapes ({formulas} range formulas) median {median:F1} ms (runs: {string.Join(", ", times.Select(t => t.ToString("F1", CultureInfo.InvariantCulture)))})");
        Assert.Equal(99_999, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(CellValue.FromNumber(100_006), wb.GetValue("A100000"));
        Assert.True(median <= 250, $"chain edit median {median:F1} ms > 250 ms");
    }

    private static string ColumnName(int column)
    {
        string name = "";
        for (; column > 0; column = (column - 1) / 26) name = (char)('A' + (column - 1) % 26) + name;
        return name;
    }
}
