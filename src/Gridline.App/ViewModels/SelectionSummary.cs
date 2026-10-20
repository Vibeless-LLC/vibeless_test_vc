using System.Globalization;
using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.App.ViewModels;

/// <summary>The status-bar SelectionSum (§11.7, ruling R43): the sum of the Number values in the selection.</summary>
public static class SelectionSummary
{
    /// <summary>Selections with at most this many cells are summed by probing each address; larger ones by filtering the
    /// used cells, so the work is bounded by min(area, used cells) and a whole-sheet selection never walks 70 million cells.</summary>
    internal const long ProbeLimit = 4096;

    /// <summary>R43 text: the sum printed with ToString("R", InvariantCulture), "0" when there are no numbers, -0 as 0.</summary>
    public static string FormatSum(Workbook workbook, CellRange selection) => Format(Sum(workbook, selection));

    public static string Format(double sum) => (sum == 0 ? 0.0 : sum).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Adds the Number values of the used cells inside <paramref name="selection"/> in row-major order (both
    /// strategies visit cells in the same order, so the floating-point result does not depend on the strategy).</summary>
    public static double Sum(Workbook workbook, CellRange selection)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        double sum = 0;
        if (selection.CellCount <= ProbeLimit)
        {
            for (int row = selection.TopLeft.Row; row <= selection.BottomRight.Row; row++)
                for (int column = selection.TopLeft.Column; column <= selection.BottomRight.Column; column++)
                    sum += NumberOrZero(workbook.GetValue(new CellAddress(column, row).ToString()));
            return sum;
        }
        foreach (string address in workbook.GetUsedAddresses()) // row-major (§9.1)
        {
            if (selection.Contains(CellAddress.Parse(address)))
                sum += NumberOrZero(workbook.GetValue(address));
        }
        return sum;
    }

    private static double NumberOrZero(CellValue v) => v.Kind == ValueKind.Number ? v.Number : 0;
}
