using System.Collections.Immutable;
using Gridline.Core.Addressing;

namespace Gridline.Core.Commands;

/// <summary>
/// One undo step (§9.3): everything a single mutating call changed, recorded so that undo restores the exact prior
/// state and redo reapplies it. Immutable.
/// </summary>
/// <remarks>
/// SetCell, ApplyBatch and CopyPaste steps are pure cell-input edit sets: each touched cell (sheet and address) appears once with its prior
/// and new input (enforced by the constructor). DefineName and DeleteName steps (§9.6) carry one <see cref="NameEdit"/>
/// and no cell edits; the names payload is a list so a later structure edit (§9.5), which may adjust several name
/// targets at once, records them in the same shape. Insert/delete rows/columns steps (§9.5) carry a
/// <see cref="StructureEdit"/> whole-sheet restructure payload, the names it adjusted, and no cell edits. An AddSheet step
/// (CR1.3) carries a <see cref="Commands.SheetInsert"/> and the cells that fill the new sheet (none for AddSheet). A
/// RenameSheet, DeleteSheet or MoveSheet step carries a <see cref="Commands.SheetChange"/>, the formulas it rewrote as
/// cell edits, and the names it rewrote (D-54). Every
/// cell edit names its sheet by stable internal id (<see cref="CellEdit"/>, D-52). The history
/// (<see cref="UndoHistory"/>) treats steps as opaque, so it needs no change. See DECISIONS.md D-34, D-41 and D-43.
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
            var seen = new HashSet<(int, CellAddress)>(cellEdits.Length);
            foreach (var edit in cellEdits)
                if (!seen.Add((edit.Sheet, edit.Address)))
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

    /// <param name="structure">The insert/delete payload (§9.5).</param>
    /// <param name="nameEdits">Each name whose refersTo the edit adjusted, once, with prior and new definition.</param>
    /// <exception cref="ArgumentException">A name appears more than once (as above), or an address appears more than
    /// once among the structure payload's rewrites or among its removed cells.</exception>
    public UndoStep(StructureEdit structure, ImmutableArray<NameEdit> nameEdits) : this([], nameEdits)
    {
        ArgumentNullException.ThrowIfNull(structure);
        if (structure.Rewrites.IsDefault) throw new ArgumentNullException(nameof(structure), "Rewrites is default.");
        if (structure.Removed.IsDefault) throw new ArgumentNullException(nameof(structure), "Removed is default.");
        RequireDistinct(structure.Rewrites, nameof(structure));
        RequireDistinct(structure.Removed, nameof(structure));
        Structure = structure;
    }

    /// <summary>A step that creates a sheet (CR1.3 AddSheet; later ImportInto), with <paramref name="cellEdits"/> filling
    /// it (each on the new sheet, prior input "") and no name edits (D-52).</summary>
    internal UndoStep(SheetInsert sheet, ImmutableArray<CellEdit> cellEdits) : this(cellEdits, [])
    {
        ArgumentNullException.ThrowIfNull(sheet);
        foreach (var edit in cellEdits)
            if (edit.Sheet != sheet.Id)
                throw new ArgumentException($"Cell {edit.Address} is not on the inserted sheet.", nameof(cellEdits));
        SheetInsert = sheet;
    }

    /// <summary>A step that renames, deletes or moves an existing sheet (CR1.3, D-54): <paramref name="rewrites"/> are the
    /// formulas it rewrote, none on a deleted sheet; <paramref name="nameEdits"/> the names it rewrote.</summary>
    /// <exception cref="ArgumentException">An address or name appears twice, a rewrite lies on the deleted sheet, a
    /// removed cell does not, or a non-delete change carries removed cells.</exception>
    internal UndoStep(SheetChange change, ImmutableArray<CellEdit> rewrites, ImmutableArray<NameEdit> nameEdits)
        : this(rewrites, nameEdits)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Removed.IsDefault) throw new ArgumentNullException(nameof(change), "Removed is default.");
        RequireDistinct(change.Removed, nameof(change));
        bool delete = change.Kind == SheetChangeKind.Delete;
        if (!delete && change.Removed.Length > 0)
            throw new ArgumentException("Only a sheet deletion removes cells.", nameof(change));
        foreach (var edit in change.Removed)
            if (edit.Sheet != change.Id)
                throw new ArgumentException($"Removed cell {edit.Address} is not on the deleted sheet.", nameof(change));
        if (delete)
            foreach (var edit in rewrites)
                if (edit.Sheet == change.Id)
                    throw new ArgumentException($"Rewritten cell {edit.Address} lies on the deleted sheet.", nameof(rewrites));
        SheetChange = change;
    }

    private static void RequireDistinct(ImmutableArray<CellEdit> edits, string paramName)
    {
        var seen = new HashSet<(int, CellAddress)>(edits.Length);
        foreach (var edit in edits)
            if (!seen.Add((edit.Sheet, edit.Address)))
                throw new ArgumentException($"Cell {edit.Address} appears more than once in one undo step.", paramName);
    }

    /// <summary>The cell inputs this step changed, each address once.</summary>
    public ImmutableArray<CellEdit> CellEdits { get; }

    /// <summary>The names this step defined, redefined or deleted, each name once; empty for cell-only steps.</summary>
    public ImmutableArray<NameEdit> NameEdits { get; }

    /// <summary>The insert/delete rows/columns payload (§9.5); null for every other step. When set,
    /// <see cref="CellEdits"/> is empty.</summary>
    public StructureEdit? Structure { get; }

    /// <summary>The sheet this step creates (CR1.3, D-52); null for every other step.</summary>
    internal SheetInsert? SheetInsert { get; }

    /// <summary>The sheet this step renames, deletes or moves (CR1.3, D-54); null for every other step. When set,
    /// <see cref="CellEdits"/> holds the rewritten formulas.</summary>
    internal SheetChange? SheetChange { get; }
}
