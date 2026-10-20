using System.Diagnostics;
using System.Globalization;
using FlaUI.Core.AutomationElements;
using Xunit.Abstractions;
using static Gridline.App.Tests.UI.AppUnderTest;

namespace Gridline.App.Tests.UI;

/// <summary>
/// P-6 (§12): GUI open of a 100k-cell file until the grid is rendered, ≤ 3 s. Measured from process launch with the file
/// argument (R44) until the grid's UIA ItemStatus reports <c>Rendered:&lt;n&gt;</c> (D-40), one warm-up and the median
/// of 5 runs. Release only, like the other §12 gates (D-30).
/// </summary>
[Collection(UiCollection.Name)]
[Trait("Category", "UI")]
[Trait("Category", "Performance")]
public sealed class P6PerformanceUiTests(ITestOutputHelper output) : IDisposable
{
    private readonly ITestOutputHelper _output = UiLog.Attach(output);

#if DEBUG
    private const string? ReleaseOnly = "§12 budgets are defined for Release builds";
#else
    private const string? ReleaseOnly = null;
#endif

    private const int Runs = 5;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    private readonly string _dir = TestSupport.NewTempDirectory();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>One launch: process start until the grid reports a rendered frame; then the app is closed (clean).</summary>
    private static TimeSpan LaunchUntilRendered(string path)
    {
        var sw = Stopwatch.StartNew();
        using var app = Launch(path);
        string status = "";
        while (sw.Elapsed < UiTimeout)
        {
            try
            {
                var window = app.Application.GetAllTopLevelWindows(app.Automation)
                    .FirstOrDefault(w => (w.Title ?? "") == "Gridline - p6.gridline");
                var grid = window?.FindFirstDescendant(cf => cf.ByAutomationId("Grid"));
                status = grid?.Properties.ItemStatus.ValueOrDefault ?? "";
                if (status.StartsWith("Rendered:", StringComparison.Ordinal)) return sw.Elapsed;
            }
            catch (Exception) { /* the window is still being created */ }
            Thread.Sleep(5);
        }
        throw new TimeoutException($"The grid did not report a rendered frame; last status '{status}'.");
    }

    [Fact(Skip = ReleaseOnly)]
    public void P6_GuiOpen100kCells_UntilGridRendered_Within3s()
    {
        string path = Path.Combine(_dir, "p6.gridline");
        Fixtures.Write100kCells(path);

        TimeSpan warmUp = LaunchUntilRendered(path);
        var runs = new List<double>();
        for (int i = 0; i < Runs; i++) runs.Add(LaunchUntilRendered(path).TotalMilliseconds);
        runs.Sort();
        double median = runs[Runs / 2];

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"P-6 warm-up {warmUp.TotalMilliseconds:F0} ms; runs {string.Join(", ", runs.Select(r => r.ToString("F0", CultureInfo.InvariantCulture)))} ms; median {median:F0} ms"));
        Assert.True(median <= Budget.TotalMilliseconds, $"P-6 median {median:F0} ms exceeds {Budget.TotalMilliseconds} ms");
    }
}
