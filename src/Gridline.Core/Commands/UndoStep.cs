using System.Collections.Immutable;
using Gridline.Core.Addressing;

namespace Gridline.Core.Commands;

/// <summary>
/// One undo step (§9.3): everything a single mutating call changed, recorded so that undo restores the exact prior
/// state and redo reapplies it. Immutable.
/// </summary>
/// <remarks>
/// SetCell, ApplyBatch and CopyPaste steps are pure cell-input edit sets: each touched cell appears once with its prior
/// and new input (enforced by the constructor). DefineName and DeleteName steps (§9.6) carry one <see cref="NameEdit"/>
/// and no cell edits; the names payload is a list so a later structure edit (§9.5), which may adjust several name
/// targets at once, records them in the same shape. The history (<see cref="UndoHistory"/>) treats steps as opaque, so
/// it needs no change. See DECISIONS.md D-34 and D-41.
/// </remarks>
public sealed class UndoStep
{
    /// <param name="cellEdits">Each touched cell once, with prior and new input.</param>
    /// <exception cref="ArgumentException">An address appears more than once. The workbook's apply and rollback path
    /// replaces each edited cell exactly once, so a duplicate would make rollback and undo restore the wrong input.
    /// The check is skipped for a single edit (SetCell stays O(1), P-3) and is O(n) otherwise.</exception>
    public UndoStep(ImmutableArray<CellEdit> cellEdits) : this(cellEdits, []) { }

    /// <param name="cellEdits">Each touched cell once, with prior and new input.</param>
    /// <param name="nameEdits">Each changed name once (case-insensitively), with prior and new definition.</param>
    /// <exception cref="ArgumentException">An address or a name appears more than once, a name edit has neither side,
    /// or its two sides are different names.</exception>
    public UndoStep(ImmutableArray<CellEdit> cellEdits, ImmutableArray<NameEdit> nameEdits)
    {
        if (cellEdits.IsDefault) throw new ArgumentNullException(nameof(cellEdits));
        if (nameEdits.IsDefault) throw new ArgumentNullException(nameof(nameEdits));
        if (cellEdits.Length > 1)
        {
            var seen = new HashSet<CellAddress>(cellEdits.Length);
            foreach (var edit in cellEdits)
                if (!seen.Add(edit.Address))
                    throw new ArgumentException($"Cell {edit.Address} appears more than once in one undo step.", nameof(cellEdits));
        }
        if (nameEdits.Length > 0)
        {
            var seenNames = new HashSet<string>(nameEdits.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var edit in nameEdits)
            {
                if (edit.Before is null && edit.After is null)
                    throw new ArgumentException("A name edit needs a prior or a new definition.", nameof(nameEdits));
                if (edit.Before is { } b && edit.After is { } a && !string.Equals(b.Name, a.Name, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Name edit '{b.Name}' -> '{a.Name}' changes the name itself.", nameof(nameEdits));
                if (!seenNames.Add(edit.Name))
                    throw new ArgumentException($"Name '{edit.Name}' appears more than once in one undo step.", nameof(nameEdits));
            }
        }
        CellEdits = cellEdits;
        NameEdits = nameEdits;
    }

    /// <summary>The cell inputs this step changed, each address once.</summary>
    public ImmutableArray<CellEdit> CellEdits { get; }

    /// <summary>The names this step defined, redefined or deleted, each name once; empty for cell-only steps.</summary>
    public ImmutableArray<NameEdit> NameEdits { get; }
}
