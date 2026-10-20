namespace Gridline.Core.Commands;

/// <summary>A defined name as stored (§9.6): its definition casing and its canonical <c>refersTo</c> (an uppercase
/// absolute cell or range, or <c>#REF!</c> once its target was deleted, ruling P1).</summary>
public readonly record struct NameDefinition(string Name, string RefersTo)
{
    /// <summary>The stable internal id of the sheet the unqualified refersTo lies on (CR1.4, D-52 ruling P5-1): the sheet
    /// that was active when the name was defined; 0 (the first sheet) for loaded names. Part of equality, so the same
    /// text bound to another sheet is a different definition.</summary>
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
