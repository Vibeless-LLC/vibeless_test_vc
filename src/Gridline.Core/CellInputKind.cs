namespace Gridline.Core;

/// <summary>How raw cell input is classified (§4.3).</summary>
public enum CellInputKind
{
    Blank,
    Number,
    Text,
    Boolean,
    Formula,
}
