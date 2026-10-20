using System.Collections.Immutable;

namespace Gridline.Core.Commands;

/// <summary>Which grid axis a §9.5 structure edit inserts or deletes along.</summary>
public enum StructureAxis
{
    Rows,
    Columns,
}

/// <summary>
/// The payload of an insert/delete rows/columns undo step (§9.5, §9.3, D-34, D-43): the edit parameters plus exactly
/// what cannot be recomputed by moving cells. Moving the remaining cells is implied by the parameters, so the step costs
/// O(rewritten formulas + deleted cells), not O(used cells).
/// </summary>
/// <param name="Axis">Rows or columns.</param>
/// <param name="IsInsert">True for an insert, false for a delete.</param>
/// <param name="Start">The first inserted or deleted row/column (1-based).</param>
/// <param name="Count">How many rows/columns were inserted or deleted (≥ 1).</param>
/// <param name="Rewrites">Every formula whose text the edit re-emitted (§5.5): <see cref="CellEdit.Address"/> is its
/// position after the edit, <see cref="CellEdit.OldInput"/> its exact text before, <see cref="CellEdit.NewInput"/> the
/// rewritten text. Every other moved cell keeps its input verbatim.</param>
/// <param name="Removed">For a delete, every cell with input inside the deleted span, at its original position, with
/// its exact input as <see cref="CellEdit.OldInput"/> and "" as <see cref="CellEdit.NewInput"/>; empty for an insert.</param>
public sealed record StructureEdit(
    StructureAxis Axis, bool IsInsert, int Start, int Count, ImmutableArray<CellEdit> Rewrites, ImmutableArray<CellEdit> Removed);
