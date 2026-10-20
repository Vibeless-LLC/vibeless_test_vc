using Gridline.Core.Addressing;

namespace Gridline.Core.Dependencies;

/// <summary>
/// One side of a resized-rectangle dependency (R68): a literal reference from the formula text, or an upper-cased name
/// (R20) whose target is looked up when the rectangle is computed.
/// </summary>
internal readonly record struct ReferenceSpec(CellRange? Literal, string? Name);

/// <summary>
/// The static dependency of a conditional aggregation call such as <c>SUMIF(range, criteria, sum_range)</c> on the rectangle
/// anchored at the top-left of <c>sum_range</c> with the dimensions of <c>range</c> (§7.5, R68, D-45).
/// </summary>
internal readonly record struct ResizedReference(ReferenceSpec Range, ReferenceSpec Anchor)
{
    /// <summary>The rectangle given the two targets; the rectangle is clipped to the grid (positions beyond it are ignored, R68).</summary>
    public static CellRange Resize(CellRange range, CellRange anchor)
    {
        int width = range.BottomRight.Column - range.TopLeft.Column;
        int height = range.BottomRight.Row - range.TopLeft.Row;
        var end = new CellAddress(
            (int)Math.Min((long)anchor.TopLeft.Column + width, GridBounds.MaxColumns),
            (int)Math.Min((long)anchor.TopLeft.Row + height, GridBounds.MaxRows));
        return new CellRange(anchor.TopLeft, end);
    }
}
