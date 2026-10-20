using System.Diagnostics;
using System.Globalization;
using Gridline.Core;
using Xunit.Abstractions;

namespace Gridline.IO.Tests;

/// <summary>Serialises the performance gates (and keeps them away from other test classes) so that
/// timings are not disturbed by parallel tests in this assembly (cross-assembly contention: see DECISIONS D-30).</summary>
[CollectionDefinition("Performance", DisableParallelization = true)]
public class PerformanceCollection;

/// <summary>
/// The Phase 1 release performance gates (§12), one test per budget: P-1, P-2, P-3, P-4, P-5, P-7.
/// P-6 (GUI) is out of scope for Phase 1. Budgets are defined for Release builds, so the gates are skipped in Debug.
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
    private static List<KeyValuePair<string, string>> HalfConstantsHalfFormulas()
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

    /// <summary>
    /// P-7 (§12): load a 100,000-formula file in a dedicated child process (tests/Gridline.PerfProbe) and read its
    /// private memory there. A fresh process contains only the runtime, the engine and the loaded workbook, so the
    /// reading is not inflated by the xunit host, the other tests' garbage or the fixture builder (DECISIONS D-30).
    /// The child does one warm-up load and then <see cref="Runs"/> timed loads, forces two full GCs with the last
    /// workbook alive, and prints "ms,ms,...;privateMB;cellCount".
    /// </summary>
    [Fact(Skip = ReleaseOnly)]
    public async Task P7_Load100kFormulas_Within5s_And600MB()
    {
        string path = TempFile();
        try
        {
            var builder = Workbook.FromCells(AllFormulas());
            GridlineFile.SaveToPath(builder, path);
            builder = null!;

            string probe = Path.Combine(AppContext.BaseDirectory, "Gridline.PerfProbe.dll");
            Assert.True(File.Exists(probe), "Gridline.PerfProbe.dll not found next to the test assembly: " + probe);
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(probe);
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add(Runs.ToString(CultureInfo.InvariantCulture));
            using var child = Process.Start(psi)!;
            var stderrTask = child.StandardError.ReadToEndAsync();
            var stdoutTask = child.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await child.WaitForExitAsync(timeout.Token);
            string stdout = await stdoutTask;
            Assert.True(child.ExitCode == 0, "P-7 probe failed: " + await stderrTask);

            var parts = stdout.Trim().Split(';');
            Assert.Equal(3, parts.Length);
            var times = parts[0].Split(',').Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToList();
            double mb = double.Parse(parts[1], CultureInfo.InvariantCulture);
            Assert.Equal(100_000, int.Parse(parts[2], CultureInfo.InvariantCulture));
            Assert.Equal(Runs, times.Count);
            double median = Median(times);
            output.WriteLine($"P-7 load 100k formulas (child process) median {median:F1} ms (runs: {string.Join(", ", times.Select(t => t.ToString("F1")))}); private memory after load+GC {mb:F1} MB");
            Assert.True(median <= 5000, $"P-7 load median {median:F1} ms > 5000 ms");
            Assert.True(mb <= 600, $"P-7 private memory {mb:F1} MB > 600 MB");
        }
        finally { DeleteQuietly(path); }
    }
}
