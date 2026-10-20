using System.Collections.Immutable;

namespace Gridline.Core.Commands;

/// <summary>
/// One undo step (§9.3): everything a single mutating call changed, recorded so that undo restores the exact prior
/// state and redo reapplies it. Immutable.
/// </summary>
/// <remarks>
/// Phase 2 steps (SetCell, ApplyBatch, CopyPaste) are pure cell-input edit sets: each touched cell appears once with
/// its prior and new input. Phase 4 name and structure edits extend this type with optional members (a names
/// snapshot delta, a whole-sheet restructure) alongside <see cref="CellEdits"/>, which may then be empty; the history
/// (<see cref="UndoHistory"/>) treats steps as opaque, so it needs no change. See DECISIONS.md D-34.
/// </remarks>
public sealed class UndoStep
{
    /// <param name="cellEdits">Each touched cell once, with prior and new input.</param>
    public UndoStep(ImmutableArray<CellEdit> cellEdits)
    {
        if (cellEdits.IsDefault) throw new ArgumentNullException(nameof(cellEdits));
        CellEdits = cellEdits;
    }

    /// <summary>The cell inputs this step changed, each address once.</summary>
    public ImmutableArray<CellEdit> CellEdits { get; }
}
