using Gridline.Core.Addressing;

namespace Gridline.Core.Evaluation;

/// <summary>
/// Abstract source of cell values for evaluation (§6.1, §6.6), so Evaluation does not depend on Dependencies or the
/// Workbook. A truly blank (empty) cell MUST be returned as <see cref="CellValue.Blank"/>; any cell with content
/// (including text "" entered as a lone apostrophe, or a formula) returns a non-Blank value, because formula results
/// are never Blank (§6.6). So <c>GetValue(a).Kind == ValueKind.Blank</c> means exactly "the cell is truly blank".
/// </summary>
public interface ICellValueProvider
{
    /// <summary>The current value of the cell at <paramref name="address"/>; Blank when the cell is empty.</summary>
    CellValue GetValue(CellAddress address);
}
