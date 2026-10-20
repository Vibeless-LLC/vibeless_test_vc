using System.Globalization;
using Gridline.Core;
using Gridline.IO;

namespace Gridline.App.Tests.UI;

/// <summary>Workbook files for the FlaUI acceptance tests.</summary>
internal static class Fixtures
{
    /// <summary>The P-1 fixture layout (R32, D-30) used for P-6 and the phase story: A1..A50000 are the numbers 1..50000,
    /// A50001..A100000 are <c>=A{r-50000}*2</c>; 100,000 cells in all.</summary>
    public static void Write100kCells(string path)
    {
        var cells = new List<KeyValuePair<string, string>>(100_000);
        for (int r = 1; r <= 50_000; r++)
            cells.Add(new("A" + r.ToString(CultureInfo.InvariantCulture), r.ToString(CultureInfo.InvariantCulture)));
        for (int r = 50_001; r <= 100_000; r++)
            cells.Add(new("A" + r.ToString(CultureInfo.InvariantCulture), "=A" + (r - 50_000).ToString(CultureInfo.InvariantCulture) + "*2"));
        GridlineFile.SaveToPath(Workbook.FromCells(cells), path);
    }
}
