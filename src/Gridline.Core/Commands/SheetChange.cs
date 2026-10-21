using System.Collections.Immutable;

namespace Gridline.Core.Commands;

/// <summary>The kind of an existing-sheet step (CR1.3, D-54).</summary>
internal enum SheetChangeKind
{
    Rename,
    Delete,
    Move,
}

/// <summary>
/// The payload of a step that renames, deletes or moves an existing sheet (CR1.3, §9.3, D-54). The sheet is named by its
/// stable internal <see cref="Id"/>. The step's <see cref="UndoStep.CellEdits"/> are the formulas the change rewrote on
/// the remaining sheets (verbatim old text, re-emitted new text, R85) and its <see cref="UndoStep.NameEdits"/> the names
/// it rewrote (rename: the new prefix, same sheet id; delete: <c>#REF!</c>).
/// <list type="bullet">
/// <item>Rename: <see cref="OldName"/> → <see cref="NewName"/>; the indexes are the sheet's (unchanged) index.</item>
/// <item>Delete: the sheet named <see cref="OldName"/> at <see cref="OldIndex"/>, with its cells in <see cref="Removed"/>
/// (each with its input as <see cref="CellEdit.OldInput"/> and "" as the new one). Undo re-creates it with the same id at
/// that index.</item>
/// <item>Move: from <see cref="OldIndex"/> to <see cref="NewIndex"/> (an index in the order after the move).</item>
/// </list>
/// </summary>
internal sealed record SheetChange(SheetChangeKind Kind, int Id, string OldName, string NewName, int OldIndex, int NewIndex)
{
    /// <summary>Delete only: the deleted sheet's cells, each once; empty for the other kinds.</summary>
    public ImmutableArray<CellEdit> Removed { get; init; } = [];
}
