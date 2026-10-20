using Gridline.Core.Addressing;

namespace Gridline.Core.Dependencies;

/// <summary>The static precedents of one formula (§6.1): distinct cells, normalised ranges and upper-cased names (R20), in first-appearance order.</summary>
public sealed class FormulaPrecedents
{
    /// <summary>No precedents (constants-only formulas and failed parses).</summary>
    public static FormulaPrecedents Empty { get; } = new([], [], [], null);

    internal FormulaPrecedents(CellAddress[] cells, CellRange[] ranges, string[] names, ResizedReference[]? resized = null)
    {
        Cells = cells;
        Ranges = ranges;
        Names = names;
        Resized = resized ?? [];
    }

    /// <summary>
    /// Resized-rectangle dependencies that cannot be fixed from the text because a name is involved (R68, D-45):
    /// SUMIF/AVERAGEIF calls whose <c>range</c> and <c>sum_range</c> are references and at least one is a name. The
    /// dependency graph turns each into a rectangle from the names' current targets. When both are literal references
    /// the rectangle is already in <see cref="Ranges"/>.
    /// </summary>
    internal IReadOnlyList<ResizedReference> Resized { get; }

    public IReadOnlyList<CellAddress> Cells { get; }
    /// <remarks>May include the synthesized SUMIF/AVERAGEIF resized rectangle (R68), not only ranges written in the text.</remarks>
    public IReadOnlyList<CellRange> Ranges { get; }
    public IReadOnlyList<string> Names { get; }
    public bool IsEmpty => Cells.Count == 0 && Ranges.Count == 0 && Names.Count == 0;
}
