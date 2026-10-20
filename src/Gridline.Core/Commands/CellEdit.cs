using Gridline.Core.Addressing;

namespace Gridline.Core.Commands;

/// <summary>
/// One cell's change within an <see cref="UndoStep"/> (§9.3): the exact input before and after, verbatim ("" is an
/// empty cell). Undo restores <see cref="OldInput"/>; redo reapplies <see cref="NewInput"/>.
/// </summary>
public readonly record struct CellEdit(CellAddress Address, string OldInput, string NewInput)
{
    /// <summary>The stable internal id of the cell's sheet (CR1.1, D-52); 0 is the first sheet of a workbook.</summary>
    internal int Sheet { get; init; }
}
