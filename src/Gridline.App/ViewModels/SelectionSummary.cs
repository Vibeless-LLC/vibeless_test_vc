using System.Globalization;
using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.App.ViewModels;

/// <summary>The status-bar SelectionSum (§11.7, ruling R43): the sum of the Number values in the selection.</summary>
public static class SelectionSummary
{
    /// <summary>Selections with at most this many cells are always probed, without asking the workbook for its used
    /// cells (GetUsedAddresses sorts every used cell, which would cost milliseconds per arrow key on a 100k-cell sheet).</summary>
    internal const long ProbeLimit = 4096;

    /// <summary>R43 text: the sum printed with ToString("R", InvariantCulture), "0" when there are no numbers, -0 as 0.</summary>
    public static string FormatSum(Workbook workbook, CellRange selection) => Format(Sum(workbook, selection));

    public static string Format(double sum) => (sum == 0 ? 0.0 : sum).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>True when the selection's cells are probed one by one; false when the used cells are filtered instead.
    /// The cheaper of the two is chosen, so the work is O(min(area, used cells)).</summary>
    internal static bool ProbesSelection(long area, int usedCount) => area <= ProbeLimit || area <= usedCount;

    /// <summary>Adds the Number values of the used cells inside <paramref name="selection"/> in row-major order (both
    /// strategies visit cells in the same order, so the floating-point result does not depend on the strategy).</summary>
    public static double Sum(Workbook workbook, CellRange selection)
    {
        double sum = 0;
        foreach (string address in UsedCellsIn(workbook, selection))
            sum += NumberOrZero(workbook.GetValue(address));
        return sum;
    }

    /// <summary>The canonical addresses of the cells with input inside <paramref name="selection"/>, row-major. The work is
    /// O(min(area, used cells)) (see <see cref="ProbesSelection"/>), so a whole-sheet selection never walks 70.2 million
    /// addresses. Used by SelectionSum (R43) and by Delete (§11.4), which clears exactly these cells.</summary>
    public static List<string> UsedCellsIn(Workbook workbook, CellRange selection)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        long area = selection.CellCount;
        IReadOnlyList<string>? used = area <= ProbeLimit ? null : workbook.GetUsedAddresses();
        var result = new List<string>();
        if (ProbesSelection(area, used?.Count ?? 0))
        {
            for (int row = selection.TopLeft.Row; row <= selection.BottomRight.Row; row++)
                for (int column = selection.TopLeft.Column; column <= selection.BottomRight.Column; column++)
                {
                    string address = new CellAddress(column, row).ToString();
                    if (workbook.GetInput(address).Length != 0) result.Add(address);
                }
            return result;
        }
        foreach (string address in used!) // row-major (§9.1)
        {
            if (selection.Contains(CellAddress.Parse(address))) result.Add(address);
        }
        return result;
    }

    private static double NumberOrZero(CellValue v) => v.Kind == ValueKind.Number ? v.Number : 0;
}
