using Gridline.Core.Addressing;

namespace Gridline.Core.Dependencies;

/// <summary>The static precedents of one formula (§6.1): distinct cells, normalised ranges and upper-cased names (R20), in first-appearance order.</summary>
public sealed class FormulaPrecedents
{
    /// <summary>No precedents (constants-only formulas and failed parses).</summary>
    public static FormulaPrecedents Empty { get; } = new([], [], []);

    internal FormulaPrecedents(CellAddress[] cells, CellRange[] ranges, string[] names)
    {
        Cells = cells;
        Ranges = ranges;
        Names = names;
    }

    public IReadOnlyList<CellAddress> Cells { get; }
    public IReadOnlyList<CellRange> Ranges { get; }
    public IReadOnlyList<string> Names { get; }
    public bool IsEmpty => Cells.Count == 0 && Ranges.Count == 0 && Names.Count == 0;
}
