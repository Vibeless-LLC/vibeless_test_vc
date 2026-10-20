namespace Gridline.App.Controls;

/// <summary>Horizontal alignment of a cell's display text (§11.3).</summary>
public enum CellAlignment
{
    Left,
    Right,
    Center,
}

/// <summary>What the grid draws for one cell: the display text and its alignment (§11.3).</summary>
public readonly record struct CellDisplay(string Text, CellAlignment Alignment)
{
    /// <summary>A blank cell: no text, left-aligned.</summary>
    public static CellDisplay Empty => new("", CellAlignment.Left);
}
