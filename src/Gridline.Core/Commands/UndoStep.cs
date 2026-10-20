using System.Collections.Immutable;
using Gridline.Core.Addressing;

namespace Gridline.Core.Commands;

/// <summary>
/// One undo step (§9.3): everything a single mutating call changed, recorded so that undo restores the exact prior
/// state and redo reapplies it. Immutable.
/// </summary>
/// <remarks>
/// Phase 2 steps (SetCell, ApplyBatch, CopyPaste) are pure cell-input edit sets: each touched cell appears once with
/// its prior and new input (enforced by the constructor). Phase 4 name and structure edits extend this type with
/// optional members (a names snapshot delta, a whole-sheet restructure) alongside <see cref="CellEdits"/>, which may
/// then be empty; the history (<see cref="UndoHistory"/>) treats steps as opaque, so it needs no change. See
/// DECISIONS.md D-34.
/// </remarks>
public sealed class UndoStep
{
    /// <param name="cellEdits">Each touched cell once, with prior and new input.</param>
    /// <exception cref="ArgumentException">An address appears more than once. The workbook's apply and rollback path
    /// replaces each edited cell exactly once, so a duplicate would make rollback and undo restore the wrong input.
    /// The check is skipped for a single edit (SetCell stays O(1), P-3) and is O(n) otherwise.</exception>
    public UndoStep(ImmutableArray<CellEdit> cellEdits)
    {
        if (cellEdits.IsDefault) throw new ArgumentNullException(nameof(cellEdits));
        if (cellEdits.Length > 1)
        {
            var seen = new HashSet<CellAddress>(cellEdits.Length);
            foreach (var edit in cellEdits)
                if (!seen.Add(edit.Address))
                    throw new ArgumentException($"Cell {edit.Address} appears more than once in one undo step.", nameof(cellEdits));
        }
        CellEdits = cellEdits;
    }

    /// <summary>The cell inputs this step changed, each address once.</summary>
    public ImmutableArray<CellEdit> CellEdits { get; }
}
