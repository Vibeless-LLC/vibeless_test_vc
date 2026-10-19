namespace Gridline.Core;

/// <summary>The kind of value held by a <see cref="CellValue"/> (§9.1).</summary>
public enum ValueKind
{
    Blank,
    Number,
    Text,
    Boolean,
    Error,
}
