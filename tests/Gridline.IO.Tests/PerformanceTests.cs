using System.Diagnostics;
using Xunit.Abstractions;
using Gridline.Core;

namespace Gridline.IO.Tests;

/// <summary>P-5 (save 100k cells &lt;= 1 s) and P-1 (load 100k cells &lt;= 1.5 s), §12. Enforced in Release builds only.</summary>
public class PerformanceTests(ITestOutputHelper output)
{
    private static Workbook Build100k()
    {
        var cells = new List<KeyValuePair<string, string>>(100_000);
        for (int r = 1; r <= 10_000; r++)
            for (int c = 1; c <= 10; c++)
            {
                string a = new Core.Addressing.CellAddress(c, r).ToString();
                cells.Add(new(a, c == 1 ? r.ToString() : c == 2 ? "=A" + r + "*2" : "text \"" + r + "\" é"));
            }
        return Workbook.FromCells(cells);
    }

    [Fact]
    public void Save_And_Load_100kCells_WithinBudget()
    {
        var wb = Build100k();
        Assert.Equal(100_000, wb.GetUsedAddresses().Count);
        GridlineSerializer.Deserialize(GridlineSerializer.Serialize(wb)); // warm-up

        var save = new List<double>();
        var load = new List<double>();
        byte[] bytes = [];
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            bytes = GridlineSerializer.Serialize(wb);
            save.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            GridlineSerializer.Deserialize(bytes);
            load.Add(sw.Elapsed.TotalMilliseconds);
        }
        save.Sort();
        load.Sort();
        output.WriteLine($"100k cells: {bytes.Length} bytes, save median {save[2]:F0} ms, load median {load[2]:F0} ms");
#if !DEBUG
        Assert.True(save[2] <= 1000, $"P-5 save median {save[2]} ms");
        Assert.True(load[2] <= 1500, $"P-1 load median {load[2]} ms");
#endif
    }
}
