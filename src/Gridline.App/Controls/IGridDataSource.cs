using Gridline.Core.Addressing;

namespace Gridline.App.Controls;

/// <summary>
/// What <see cref="GridControl"/> reads and drives (§11.3). The grid pulls display values on demand for the cells it
/// draws, so the source never materialises the sheet; selection lives in the source so the name box, formula bar and
/// status bar follow it.
/// </summary>
public interface IGridDataSource
{
    /// <summary>Display text and alignment for the cell at 1-based <paramref name="column"/>, <paramref name="row"/>.</summary>
    CellDisplay GetDisplay(int column, int row);

    /// <summary>The active cell.</summary>
    CellAddress ActiveCell { get; }

    /// <summary>The selected rectangle; always contains <see cref="ActiveCell"/>.</summary>
    CellRange Selection { get; }

    /// <summary>Moves the active cell by the given deltas, clamped to the grid. With <paramref name="extend"/> the anchor stays
    /// put and the selection grows or shrinks (Shift+arrows); otherwise the selection collapses to the new active cell.</summary>
    void MoveActiveCell(int columnDelta, int rowDelta, bool extend);

    /// <summary>Makes <paramref name="cell"/> active; with <paramref name="extend"/> the anchor is kept (Shift+click, drag).</summary>
    void SelectCell(CellAddress cell, bool extend);

    /// <summary>The bottom-right corner of the used area (largest used row and column), or A1 on an empty sheet.</summary>
    CellAddress GetLastUsedCell();

    /// <summary>Raised when displayed values may have changed (Workbook.CellsChanged, a new workbook).</summary>
    event EventHandler? ContentChanged;

    /// <summary>Raised when <see cref="ActiveCell"/> or <see cref="Selection"/> changed.</summary>
    event EventHandler? SelectionChanged;
}
