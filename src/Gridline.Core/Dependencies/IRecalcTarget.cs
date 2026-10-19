using System.Diagnostics.CodeAnalysis;
using Gridline.Core.Addressing;
using Gridline.Core.Evaluation;
using Gridline.Core.Parsing;

namespace Gridline.Core.Dependencies;

/// <summary>
/// The cell store that <see cref="DependencyGraph"/> recalculates (§6.1, §6.7); the Workbook implements it.
/// <see cref="ICellValueProvider.GetValue"/> must return each cell's current value; for a formula cell that is its
/// last computed value, which the store keeps until <see cref="SetComputedValue"/> replaces it (so the graph can
/// report which values changed).
/// </summary>
public interface IRecalcTarget : ICellValueProvider
{
    /// <summary>The parsed formula of the cell at <paramref name="address"/>, if it holds a formula (a failed parse included).</summary>
    bool TryGetFormula(CellAddress address, [NotNullWhen(true)] out ParsedFormula? formula);

    /// <summary>Stores the value computed for a formula cell.</summary>
    void SetComputedValue(CellAddress address, CellValue value);
}
