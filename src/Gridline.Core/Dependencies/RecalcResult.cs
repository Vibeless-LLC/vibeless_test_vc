using Gridline.Core.Addressing;

namespace Gridline.Core.Dependencies;

/// <summary>Outcome of one recalculation (§6.1, §6.7).</summary>
public sealed class RecalcResult
{
    internal static readonly RecalcResult None = new(0, []);

    internal RecalcResult(int evaluatedFormulaCount, IReadOnlyList<CellAddress> changedCells)
    {
        EvaluatedFormulaCount = evaluatedFormulaCount;
        ChangedCells = changedCells;
    }

    /// <summary>
    /// R21: the number of formula cells in the affected set (formula cells among the changed cells plus all their
    /// transitive dependents; every formula for a full recalc), including cells set to #CIRC!. Constants never count.
    /// </summary>
    public int EvaluatedFormulaCount { get; }

    /// <summary>
    /// Formula cells whose value differs from the value they held before this recalc, in evaluation order, each once.
    /// The caller's directly edited constant cells are not included (the graph does not see constant values change).
    /// </summary>
    public IReadOnlyList<CellAddress> ChangedCells { get; }
}
