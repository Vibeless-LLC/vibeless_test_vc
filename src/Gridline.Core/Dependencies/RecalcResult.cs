using Gridline.Core.Addressing;

namespace Gridline.Core.Dependencies;

/// <summary>Outcome of one recalculation (§6.1, §6.7).</summary>
public sealed class RecalcResult
{
    internal static readonly RecalcResult None = new(0, []);

    private IReadOnlyList<CellAddress>? _changedCells;

    internal RecalcResult(int evaluatedFormulaCount, IReadOnlyList<long> changedKeys)
    {
        EvaluatedFormulaCount = evaluatedFormulaCount;
        ChangedKeys = changedKeys;
    }

    /// <summary>
    /// R21: the number of formula cells in the affected set (formula cells among the changed cells plus all their
    /// transitive dependents; every formula for a full recalc), including cells set to #CIRC!. Constants never count.
    /// </summary>
    public int EvaluatedFormulaCount { get; }

    /// <summary>
    /// Formula cells whose value differs from the value they held before this recalc, in evaluation order, each once.
    /// The caller's directly edited constant cells are not included (the graph does not see constant values change).
    /// With several sheets this lists the addresses without their sheet; see <see cref="ChangedKeys"/>.
    /// </summary>
    public IReadOnlyList<CellAddress> ChangedCells
    {
        get
        {
            if (_changedCells is null)
            {
                var cells = new CellAddress[ChangedKeys.Count];
                for (int i = 0; i < cells.Length; i++) cells[i] = SheetCellKey.Address(ChangedKeys[i]);
                _changedCells = cells;
            }
            return _changedCells;
        }
    }

    /// <summary>As <see cref="ChangedCells"/>, as sheet-qualified keys (<see cref="SheetCellKey"/>, CR1.1).</summary>
    internal IReadOnlyList<long> ChangedKeys { get; }
}
