namespace Gridline.App.Controls;

/// <summary>Which headers a whole-row or whole-column selection was made from (§11.3, §11.5, R75).</summary>
public enum HeaderAxis
{
    Rows,
    Columns,
}

/// <summary>
/// The header side of the grid's source (§11.3, §11.5, R75). A <see cref="GridControl"/> whose source also implements this
/// interface selects whole rows or columns from header clicks and offers its context menu (Insert, Delete) on headers.
/// </summary>
public interface IGridHeaderSource
{
    /// <summary>A left-click on a header: selects the whole row (A–ZZ) or column (1–100000) at <paramref name="index"/>;
    /// with <paramref name="extend"/> (Shift+click, drag) the anchor is kept and the selection grows to it.
    /// <paramref name="activeOther"/> is the active cell's other coordinate (the first visible column or row), so the view
    /// does not scroll.</summary>
    void SelectHeaders(HeaderAxis axis, int index, int activeOther, bool extend);

    /// <summary>A right-click on a header, before its context menu opens: a header outside the current whole-row or
    /// whole-column selection on that axis is selected first (R75); the menu then acts on that axis.</summary>
    /// <returns>True when the selection is then a whole-row or whole-column selection containing the header, so the menu
    /// may open; false when the selection could not change (for example a refused edit is still open).</returns>
    bool PrepareHeaderMenu(HeaderAxis axis, int index, int activeOther);

    /// <summary>True when the selection is a whole-row or whole-column header selection the context menu can act on.</summary>
    bool HasHeaderSelection { get; }
}
