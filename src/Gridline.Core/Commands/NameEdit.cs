namespace Gridline.Core.Commands;

/// <summary>A defined name as stored (§9.6, CR1.4): its definition casing and its canonical <c>refersTo</c>, a
/// sheet-qualified absolute cell or range (<c>Data!$A$1:$A$10</c>: the sheet in its defined casing, quoted only when
/// required, the cell part uppercase, R58, R77), or <c>#REF!</c> once its target was deleted (ruling P1).</summary>
public readonly record struct NameDefinition(string Name, string RefersTo)
{
    /// <summary>The stable internal id of the sheet the qualified refersTo names (CR1.4, R77, D-53), so evaluation and
    /// the dependency graph bind the target without looking the prefix text up again. For <c>#REF!</c> it is the sheet
    /// the target lay on (or the first sheet for a loaded <c>#REF!</c>) and has no meaning. Part of equality (D-53).</summary>
    internal int Sheet { get; init; }
}

/// <summary>
/// One name's change within an <see cref="UndoStep"/> (§9.3): the exact definition before and after (null = not
/// defined). Undo restores <see cref="Before"/>, redo reapplies <see cref="After"/>. When both are present they are the
/// same name case-insensitively (the casing may differ: a casing-only redefinition is a change, R60).
/// </summary>
public readonly record struct NameEdit(NameDefinition? Before, NameDefinition? After)
{
    /// <summary>The name this edit is about (either side's casing; matching is case-insensitive).</summary>
    public string Name => (After ?? Before)?.Name ?? "";
}
