using Gridline.Core.Addressing;

namespace Gridline.Core.Dependencies;

/// <summary>The static precedents of one formula (§6.1): distinct cells, normalised ranges and upper-cased names (R20), in first-appearance order.</summary>
public sealed class FormulaPrecedents
{
    /// <summary>No precedents (constants-only formulas and failed parses).</summary>
    public static FormulaPrecedents Empty { get; } = new([], [], [], null);

    internal FormulaPrecedents(CellPrecedent[] cells, RangePrecedent[] ranges, string[] names, ResizedReference[]? resized = null)
    {
        CellPrecedents = cells;
        RangePrecedents = ranges;
        Cells = Unqualified(cells, static c => c.Sheet, static c => c.Address);
        Ranges = Unqualified(ranges, static r => r.Sheet, static r => r.Range);
        Names = names;
        Resized = resized ?? [];
    }

    /// <summary>
    /// Resized-rectangle dependencies that cannot be fixed from the text because a name is involved (R68, D-45):
    /// SUMIF/AVERAGEIF calls whose <c>range</c> and <c>sum_range</c> are references and at least one is a name. The
    /// dependency graph turns each into a rectangle from the names' current targets. When both are literal references
    /// the rectangle is already in <see cref="Ranges"/> (or <see cref="RangePrecedents"/> when the anchor is qualified).
    /// </summary>
    internal IReadOnlyList<ResizedReference> Resized { get; }

    /// <summary>The unqualified cell precedents (the formula's own sheet).</summary>
    public IReadOnlyList<CellAddress> Cells { get; }

    /// <summary>The unqualified range precedents (the formula's own sheet).</summary>
    /// <remarks>May include the synthesized SUMIF/AVERAGEIF resized rectangle (R68), not only ranges written in the text.</remarks>
    public IReadOnlyList<CellRange> Ranges { get; }

    public IReadOnlyList<string> Names { get; }

    /// <summary>Every cell precedent with its sheet (CR1.2): unqualified ones (sheet null, the same as <see cref="Cells"/>)
    /// and qualified ones (upper-cased sheet), distinct, in first-appearance order.</summary>
    public IReadOnlyList<CellPrecedent> CellPrecedents { get; }

    /// <summary>Every range precedent with its sheet (CR1.2), as <see cref="CellPrecedents"/>; includes the R68 rectangle,
    /// on the sheet of the <c>sum_range</c> argument.</summary>
    public IReadOnlyList<RangePrecedent> RangePrecedents { get; }

    public bool IsEmpty => CellPrecedents.Count == 0 && RangePrecedents.Count == 0 && Names.Count == 0;

    private static TValue[] Unqualified<TItem, TValue>(TItem[] items, Func<TItem, string?> sheet, Func<TItem, TValue> value)
    {
        int count = 0;
        foreach (var item in items) if (sheet(item) is null) count++;
        if (count == 0) return [];
        var result = new TValue[count];
        int i = 0;
        foreach (var item in items) if (sheet(item) is null) result[i++] = value(item);
        return result;
    }
}
