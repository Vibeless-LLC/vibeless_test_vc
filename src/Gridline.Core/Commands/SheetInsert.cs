namespace Gridline.Core.Commands;

/// <summary>
/// The payload of a step that creates one sheet (CR1.3 AddSheet, §9.3, D-52): the sheet's stable internal id, its
/// defined name and its index in the sheet order. Forward (do, redo) inserts the sheet at <see cref="Index"/> with that
/// id; backward (undo) removes it, and the active sheet moves per R80 when it was the removed one. The sheet is created
/// empty; a step that also fills it (ImportInto, R76) adds its cells as <see cref="UndoStep.CellEdits"/> on that sheet.
/// </summary>
internal sealed record SheetInsert(int Id, string Name, int Index);
