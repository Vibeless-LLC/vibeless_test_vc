using System.Diagnostics;
using System.Globalization;
using Gridline.Core;
using Xunit.Abstractions;

namespace Gridline.IO.Tests;

/// <summary>
/// §9.4 "work in proportion to used cells, not rectangle area" (task 4 brief, §12). Same conventions as
/// <see cref="RecalcScaleGates"/>: the non-parallel "Performance" collection, Release-only (skipped in Debug), trait
/// Category=Performance, public Workbook API only, fixtures built outside the timed region. The bounds are generous;
/// the area-independence check compares two pastes of the same used cells whose rectangles differ about 35,000-fold
/// in area, so it does not depend on the machine's absolute speed.
/// </summary>
[Collection("Performance")]
[Trait("Category", "Performance")]
public class CopyPasteScaleGates(ITestOutputHelper output)
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

    private static double Time(Action action)
    {
        long t0 = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>100,000 used cells (A1:A50000 numbers, B1:B50000 "=A_r*2") pasted to C1 in one step: 50,000 formulas
    /// re-emitted, 100,000 edits, one recalc. Then undo and redo of the same step.</summary>
    [Fact(Skip = ReleaseOnly)]
    public void Paste_100kUsedCells_Within3s()
    {
        const int n = 50_000;
        var cells = new List<KeyValuePair<string, string>>(2 * n);
        for (int r = 1; r <= n; r++) cells.Add(new("A" + Num(r), Num(r)));
        for (int r = 1; r <= n; r++) cells.Add(new("B" + Num(r), "=A" + Num(r) + "*2"));
        var warm = Workbook.FromCells(cells.Take(2_000));
        warm.CopyPaste("A1:B1000", "C1");

        var wb = Workbook.FromCells(cells);
        double paste = Time(() => wb.CopyPaste("A1:B50000", "C1"));
        Assert.Equal("=C50000*2", wb.GetInput("D50000"));
        Assert.Equal(CellValue.FromNumber(2 * n), wb.GetValue("D50000"));
        Assert.Equal(n, wb.LastRecalc.EvaluatedFormulaCount);
        double undo = Time(() => Assert.True(wb.Undo()));
        Assert.Equal("", wb.GetInput("D1"));
        double redo = Time(() => Assert.True(wb.Redo()));
        Assert.Equal("=C1*2", wb.GetInput("D1"));
        output.WriteLine($"Paste of 100,000 used cells: {paste:F1} ms; undo {undo:F1} ms; redo {redo:F1} ms");
        Assert.True(paste <= 3000, $"paste {paste:F1} ms > 3000 ms");
        Assert.True(undo <= 3000, $"undo {undo:F1} ms > 3000 ms");
        Assert.True(redo <= 3000, $"redo {redo:F1} ms > 3000 ms");
    }

    /// <summary>
    /// The same 500 used source cells pasted from their tight 10 x 100 rectangle and from A1:ZZ50000 (35.1 million
    /// cells, about 35,000 times the area) onto a destination that holds 1,000 used cells (half overwritten, half
    /// cleared). An area-proportional implementation probes ~70 million cells (about 120 ms on the dev machine, against
    /// about 2 ms); the bound allows the large-area paste 5x the small one plus 25 ms (medians of five), and an absolute
    /// 500 ms. Both pastes must have the same outcome.
    /// </summary>
    [Fact(Skip = ReleaseOnly)]
    public void Paste_LargeAreaSparseSheet_ScalesWithUsedCellsNotArea()
    {
        static Workbook Sheet()
        {
            var cells = new List<KeyValuePair<string, string>>();
            for (int c = 0; c < 10; c++)
                for (int r = 1; r <= 100; r++)
                {
                    string col = ((char)('A' + c)).ToString();
                    if ((c + r) % 2 == 0) // checkerboard: 500 source cells, half of them formulas
                        cells.Add(new(col + Num(r), c % 2 == 0 ? Num(r) : "=" + (char)('A' + c - 1) + Num(r) + "+1"));
                    cells.Add(new(col + Num(50_000 + r), "old")); // 1,000 destination cells: 500 overwritten, 500 cleared
                }
            return Workbook.FromCells(cells);
        }

        Sheet().CopyPaste("A1:J100", "A50101"); // warm-up
        var small = new List<double>();
        var large = new List<double>();
        for (int i = 0; i < Runs; i++)
        {
            var a = Sheet();
            small.Add(Time(() => a.CopyPaste("A1:J100", "A50001")));
            var b = Sheet();
            large.Add(Time(() => b.CopyPaste("A1:ZZ50000", "A50001")));
            Assert.Equal(a.GetUsedAddresses(), b.GetUsedAddresses()); // same outcome either way
            Assert.Equal(a.GetUsedAddresses().Select(a.GetInput), b.GetUsedAddresses().Select(b.GetInput));
            Assert.Equal("=A50099+1", b.GetInput("B50099"));
            Assert.Equal("", b.GetInput("B50100"));
        }
        double s = Median(small), l = Median(large);
        output.WriteLine($"Paste of 500 used cells over 1,000 used destination cells: tight rectangle median {s:F2} ms, A1:ZZ50000 median {l:F2} ms");
        Assert.True(l <= 5 * s + 25, $"large-area paste {l:F2} ms is not proportional to used cells (tight {s:F2} ms)");
        Assert.True(l <= 500, $"large-area paste {l:F2} ms > 500 ms");
    }
}
