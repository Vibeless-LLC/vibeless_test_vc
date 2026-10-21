using System.Diagnostics;
using System.Globalization;
using Gridline.Core;
using Xunit.Abstractions;

namespace Gridline.IO.Tests;

/// <summary>Serialises the performance gates (and keeps them away from other test classes) so that timings and
/// the P-7 memory reading are not disturbed by parallel tests in this assembly (cross-assembly contention: DECISIONS D-30).</summary>
[CollectionDefinition("Performance", DisableParallelization = true)]
public class PerformanceCollection;

/// <summary>
/// The release performance gates (§12, CR1.9), one test per budget: P-1, P-2, P-3, P-4, P-5, P-7, P-8.
/// P-6 (GUI) is `Gridline.App.Tests.UI.P6PerformanceUiTests` (D-40). Budgets are defined for Release builds, so the gates are skipped in Debug.
/// Method: one warm-up, then the median of 5 runs (P-3: median of 1,000 edits per run, median of 5 runs).
/// Fixtures follow ruling R32 and are built in code. Budgets are never relaxed; optimise the hot path instead.
/// </summary>
[Collection("Performance")]
[Trait("Category", "Performance")]
public class PerformanceGates(ITestOutputHelper output)
{
#if DEBUG
    private const string? ReleaseOnly = "§12 budgets are defined for Release builds";
#else
    private const string? ReleaseOnly = null;
#endif

    private const int Runs = 5;

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>R32 P-1/P-5 fixture: 100,000 cells in a 100,000-row layout; rows 1..50,000 are numeric
    /// constants in column A, rows 50,001..100,000 are simple formulas "=A{r-50000}*2".</summary>
    internal static List<KeyValuePair<string, string>> HalfConstantsHalfFormulas()
    {
        var cells = new List<KeyValuePair<string, string>>(100_000);
        for (int r = 1; r <= 50_000; r++)
            cells.Add(new("A" + r, r.ToString(CultureInfo.InvariantCulture)));
        for (int r = 50_001; r <= 100_000; r++)
            cells.Add(new("A" + r, "=A" + (r - 50_000) + "*2"));
        return cells;
    }

    /// <summary>R32 P-7 fixture (worst case): 100,000 formula cells, each depending on a blank cell.</summary>
    private static List<KeyValuePair<string, string>> AllFormulas()
    {
        var cells = new List<KeyValuePair<string, string>>(100_000);
        for (int r = 1; r <= 100_000; r++)
            cells.Add(new("A" + r, "=B" + r + "*2+" + r.ToString(CultureInfo.InvariantCulture)));
        return cells;
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "gridline-perf-" + Guid.NewGuid().ToString("N") + ".gridline");

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact(Skip = ReleaseOnly)]
    public void P1_Load100kCells_Within1500ms()
    {
        var wb = Workbook.FromCells(HalfConstantsHalfFormulas());
        string path = TempFile();
        try
        {
            GridlineFile.SaveToPath(wb, path);
            GridlineFile.LoadFromPath(path); // warm-up
            var times = new List<double>();
            Workbook? loaded = null;
            for (int i = 0; i < Runs; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                loaded = GridlineFile.LoadFromPath(path);
                times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            Assert.Equal(100_000, loaded!.GetUsedAddresses().Count);
            double median = Median(times);
            output.WriteLine($"P-1 load 100k cells median {median:F1} ms (runs: {string.Join(", ", times.Select(t => t.ToString("F1")))})");
            Assert.True(median <= 1500, $"P-1 load median {median:F1} ms > 1500 ms");
        }
        finally { DeleteQuietly(path); }
    }

    [Fact(Skip = ReleaseOnly)]
    public void P2_ChainEditA1_100kCells_Within250ms()
    {
        var cells = new List<KeyValuePair<string, string>>(100_000) { new("A1", "1") };
        for (int r = 2; r <= 100_000; r++) cells.Add(new("A" + r, "=A" + (r - 1) + "+1"));
        var wb = Workbook.FromCells(cells);
        Assert.Equal(99_999, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(CellValue.FromNumber(100_000), wb.GetValue("A100000"));

        int events = 0, changed = 0;
        wb.CellsChanged += (_, e) => { events++; changed = e.Addresses.Count; };
        wb.SetCell("A1", "2"); // warm-up
        var times = new List<double>();
        for (int i = 0; i < Runs; i++)
        {
            string input = (i + 3).ToString(CultureInfo.InvariantCulture);
            long t0 = Stopwatch.GetTimestamp();
            wb.SetCell("A1", input);
            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        double median = Median(times);
        output.WriteLine($"P-2 chain edit A1 median {median:F1} ms (runs: {string.Join(", ", times.Select(t => t.ToString("F1")))})");
        Assert.Equal(99_999, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(CellValue.FromNumber(100_006), wb.GetValue("A100000"));
        Assert.Equal(Runs + 1, events);
        Assert.Equal(100_000, changed);
        Assert.True(median <= 250, $"P-2 median {median:F1} ms > 250 ms");
    }

    [Fact(Skip = ReleaseOnly)]
    public void P3_SetCellNoDependents_MedianUnder1ms()
    {
        var wb = new Workbook();
        for (int r = 1; r <= 1000; r++) wb.SetCell("A" + r, "=B" + r + "*2");
        wb.CellsChanged += (_, _) => { };
        for (int i = 0; i < 200; i++) wb.SetCell("C" + (i + 1), i.ToString(CultureInfo.InvariantCulture)); // warm-up

        var medians = new List<double>();
        for (int run = 0; run < Runs; run++)
        {
            var times = new List<double>(1000);
            for (int i = 0; i < 1000; i++)
            {
                string input = (run * 1000 + i).ToString(CultureInfo.InvariantCulture);
                long t0 = Stopwatch.GetTimestamp();
                wb.SetCell("D" + (i + 1), input);
                times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            medians.Add(Median(times));
        }
        double median = Median(medians);
        output.WriteLine($"P-3 SetCell no dependents: per-run medians over 1,000 edits (ms): {string.Join(", ", medians.Select(m => m.ToString("F4")))}; median {median:F4}");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.True(median <= 1.0, $"P-3 median {median:F4} ms > 1 ms");
    }

    [Fact(Skip = ReleaseOnly)]
    public void P4_SumOver100kNumbersEdit_Within50ms()
    {
        var cells = new List<KeyValuePair<string, string>>(100_001);
        for (int r = 1; r <= 100_000; r++) cells.Add(new("A" + r, r.ToString(CultureInfo.InvariantCulture)));
        cells.Add(new("B1", "=SUM(A1:A100000)"));
        var wb = Workbook.FromCells(cells);
        Assert.Equal(CellValue.FromNumber(5_000_050_000d), wb.GetValue("B1"));

        wb.SetCell("A1", "2"); // warm-up
        var times = new List<double>();
        for (int i = 0; i < Runs; i++)
        {
            string input = (i + 3).ToString(CultureInfo.InvariantCulture);
            long t0 = Stopwatch.GetTimestamp();
            wb.SetCell("A1", input);
            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        double median = Median(times);
        output.WriteLine($"P-4 SUM edit median {median:F2} ms (runs: {string.Join(", ", times.Select(t => t.ToString("F2")))})");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(CellValue.FromNumber(5_000_050_000d - 1 + 7), wb.GetValue("B1"));
        Assert.True(median <= 50, $"P-4 median {median:F2} ms > 50 ms");
    }

    [Fact(Skip = ReleaseOnly)]
    public void P5_Save100kCells_Within1000ms()
    {
        var wb = Workbook.FromCells(HalfConstantsHalfFormulas());
        string path = TempFile();
        try
        {
            GridlineFile.SaveToPath(wb, path); // warm-up
            var times = new List<double>();
            for (int i = 0; i < Runs; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                GridlineFile.SaveToPath(wb, path);
                times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            double median = Median(times);
            output.WriteLine($"P-5 save 100k cells median {median:F1} ms, {new FileInfo(path).Length} bytes (runs: {string.Join(", ", times.Select(t => t.ToString("F1")))})");
            Assert.True(median <= 1000, $"P-5 save median {median:F1} ms > 1000 ms");
        }
        finally { DeleteQuietly(path); }
    }

    /// <summary>Internal gates (Task 7, R70): CSV import and export of the P-1 fixture, each ≤ 1.5 s.</summary>
    [Fact(Skip = ReleaseOnly)]
    public void Csv_Import_And_Export_100kCells_Within1500ms()
    {
        var wb = Workbook.FromCells(HalfConstantsHalfFormulas());
        byte[] csv = CsvFormat.Export(wb); // warm-up
        CsvFormat.Import(csv);
        var exportTimes = new List<double>();
        var importTimes = new List<double>();
        Workbook? imported = null;
        for (int i = 0; i < Runs; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            csv = CsvFormat.Export(wb);
            exportTimes.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            t0 = Stopwatch.GetTimestamp();
            imported = CsvFormat.Import(csv);
            importTimes.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        Assert.Equal(100_000, imported!.GetUsedAddresses().Count);
        Assert.Equal(CellValue.FromNumber(100_000), imported.GetValue("A100000"));
        double export = Median(exportTimes), import = Median(importTimes);
        output.WriteLine($"CSV export 100k cells median {export:F1} ms (runs: {string.Join(", ", exportTimes.Select(t => t.ToString("F1")))})");
        output.WriteLine($"CSV import 100k cells median {import:F1} ms (runs: {string.Join(", ", importTimes.Select(t => t.ToString("F1")))})");
        Assert.True(export <= 1500, $"CSV export median {export:F1} ms > 1500 ms");
        Assert.True(import <= 1500, $"CSV import median {import:F1} ms > 1500 ms");
    }

    /// <summary>P-7 (§12) is measured in-host, see DECISIONS D-30 for why that is reliable and the measured headroom.</summary>
    [Fact(Skip = ReleaseOnly)]
    public void P7_Load100kFormulas_Within5s_And600MB()
    {
        var wb = Workbook.FromCells(AllFormulas());
        string path = TempFile();
        try
        {
            GridlineFile.SaveToPath(wb, path);
            wb = null!; // drop the builder workbook so it does not count towards memory
            GridlineFile.LoadFromPath(path); // warm-up

            var times = new List<double>();
            Workbook? loaded = null;
            for (int i = 0; i < Runs; i++)
            {
                loaded = null; // release the previous load before the next one
                long t0 = Stopwatch.GetTimestamp();
                loaded = GridlineFile.LoadFromPath(path);
                times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            double mb = process.PrivateMemorySize64 / (1024.0 * 1024.0);
            double median = Median(times);
            output.WriteLine($"P-7 load 100k formulas median {median:F1} ms; private memory after load+GC {mb:F1} MB");
            GC.KeepAlive(loaded);
            Assert.Equal(100_000, loaded!.GetUsedAddresses().Count);
            Assert.True(median <= 5000, $"P-7 load median {median:F1} ms > 5000 ms");
            Assert.True(mb <= 600, $"P-7 private memory {mb:F1} MB > 600 MB");
        }
        finally { DeleteQuietly(path); }
    }

    /// <summary>
    /// P-8 fixture (CR1.9): 10 sheets × 10,000 formulas, each referencing other sheets. Sheet1 is the most referenced:
    /// Sheet1!A{r} is <c>=Sheet2!B{r}*2</c>, and Sheet{k}!A{r} (k = 2..10) is <c>=Sheet1!A{r}+Sheet{k+1}!B{r}</c> (Sheet10
    /// uses Sheet2), so 90,000 formulas name Sheet1, which has 10,000 dependents chained further.
    /// </summary>
    internal static Workbook TenSheetsReferencingSheet1()
    {
        var wb = new Workbook();
        for (int k = 2; k <= 10; k++) wb.AddSheet("Sheet" + k);
        var cells = new List<KeyValuePair<string, string>>(100_000);
        for (int r = 1; r <= 10_000; r++)
            cells.Add(new("Sheet1!A" + r, "=Sheet2!B" + r + "*2"));
        for (int k = 2; k <= 10; k++)
        {
            int other = k == 10 ? 2 : k + 1;
            for (int r = 1; r <= 10_000; r++)
                cells.Add(new("Sheet" + k + "!A" + r, "=Sheet1!A" + r + "+Sheet" + other + "!B" + r));
        }
        wb.ApplyBatch(cells);
        return wb;
    }

    /// <summary>P-8 (CR1.9): RenameSheet on the most-referenced sheet ≤ 500 ms. Each run renames Sheet1 to a name that
    /// needs quoting or back, so every run rewrites the 90,000 referencing formulas.</summary>
    [Fact(Skip = ReleaseOnly)]
    public void P8_RenameMostReferencedSheet_10SheetsOf10kFormulas_Within500ms()
    {
        var wb = TenSheetsReferencingSheet1();
        Assert.Equal(100_000, wb.GetUsedAddresses().Count);
        string[] names = ["Hub Sheet", "Sheet1"];
        wb.RenameSheet("Sheet1", names[0]); // warm-up
        string current = names[0];
        var times = new List<double>();
        for (int i = 0; i < Runs; i++)
        {
            string next = names[(i + 1) % 2];
            long t0 = Stopwatch.GetTimestamp();
            wb.RenameSheet(current, next);
            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            current = next;
        }
        double median = Median(times);
        output.WriteLine($"P-8 rename most-referenced sheet (90,000 rewrites) median {median:F1} ms; LastRecalc {wb.LastRecalc.EvaluatedFormulaCount} (runs: {string.Join(", ", times.Select(t => t.ToString("F1")))})");
        Assert.Equal("Sheet1", current); // the warm-up and an odd number of runs
        Assert.Equal("=Sheet1!A7+Sheet6!B7", wb.GetInput("Sheet5!A7"));
        wb.RenameSheet("sheet1", "Hub Sheet");
        Assert.Equal("='Hub Sheet'!A7+Sheet6!B7", wb.GetInput("Sheet5!A7"));
        Assert.Equal("=Sheet2!B7*2", wb.GetInput("'Hub Sheet'!A7"));
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount); // the rename changes no value, so nothing re-evaluates
        Assert.True(median <= 500, $"P-8 median {median:F1} ms > 500 ms");
    }
}
