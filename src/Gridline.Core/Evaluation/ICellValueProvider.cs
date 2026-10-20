using Gridline.Core.Addressing;

namespace Gridline.Core.Evaluation;

/// <summary>
/// Abstract source of cell values for evaluation (§6.1, §6.6), so Evaluation does not depend on Dependencies or the
/// Workbook. A truly blank (empty) cell MUST be returned as <see cref="CellValue.Blank"/>; any cell with content
/// (including text "" entered as a lone apostrophe, or a formula) returns a non-Blank value, because formula results
/// are never Blank (§6.6). So <c>GetValue(a).Kind == ValueKind.Blank</c> means exactly "the cell is truly blank".
/// </summary>
/// <remarks>A provider is one sheet: the sheet of the formula being evaluated. Qualified references (CR1.2) read other
/// sheets through <see cref="ResolveSheet"/>.</remarks>
public interface ICellValueProvider
{
    /// <summary>The current value of the cell at <paramref name="address"/>; Blank when the cell is empty.</summary>
    CellValue GetValue(CellAddress address);

    /// <summary>
    /// The provider of the sheet named <paramref name="sheet"/> (matched case-insensitively, R82), which may be this
    /// provider; null when no such sheet exists, so a reference to it evaluates to #REF!. The default knows no sheets.
    /// </summary>
    ICellValueProvider? ResolveSheet(string sheet) => null;
}
