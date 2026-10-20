using Gridline.Core.Addressing;
using Gridline.Core.Parsing;

namespace Gridline.Core.Commands;

/// <summary>
/// The §9.5 reference adjustment for inserting or deleting rows or columns. References point at positions, so the
/// position of the formula itself never matters, and relative and absolute components are treated the same (each
/// keeps its $ flag). Only the coordinate along <see cref="StructureAxis"/> changes:
/// <list type="bullet">
/// <item>Single cell at <c>x</c>. Insert at <c>r</c>, <c>n</c>: <c>x ≥ r</c> → <c>x + n</c>, past the bound → invalid.
/// Delete <c>[r, r+n-1]</c>: inside → invalid; after it → <c>x − n</c>.</item>
/// <item>Range <c>[lo, hi]</c>. Insert: each endpoint independently; a start past the bound → invalid, an end alone
/// past it is clamped to the bound (R61). Delete: the surviving rows are kept; none survive → invalid; otherwise the
/// endpoints are the first and last surviving positions after the shift.</item>
/// </list>
/// A range written bottom-up (<c>A5:A1</c>) keeps its orientation. <see cref="Changed"/> records whether any reference
/// printed through this rewriter differs from its input, so callers keep unchanged formulas verbatim (§9.5).
/// </summary>
internal sealed class StructureRewriter : IReferenceRewriter
{
    private readonly bool _rows;
    private readonly bool _insert;
    private readonly int _start;
    private readonly int _count;
    private readonly int _bound;

    /// <exception cref="ArgumentOutOfRangeException">Start or count is outside the valid range for the axis.</exception>
    public StructureRewriter(StructureAxis axis, bool insert, int start, int count)
    {
        _rows = axis == StructureAxis.Rows;
        _insert = insert;
        _bound = _rows ? GridBounds.MaxRows : GridBounds.MaxColumns;
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        if (start < 1 || start > _bound) throw new ArgumentOutOfRangeException(nameof(start));
        if (!insert && (long)start + count - 1 > _bound) throw new ArgumentOutOfRangeException(nameof(count));
        _start = start;
        _count = count;
    }

    /// <summary>True once a reference printed through <see cref="Rewrite"/> or <see cref="RewriteRange"/> changed or
    /// became invalid since the last <see cref="Reset"/>.</summary>
    public bool Changed { get; private set; }

    public void Reset() => Changed = false;

    /// <summary>The position of a cell (or a single reference coordinate) after the edit; -1 when it is removed by a
    /// delete or pushed past the bound by an insert.</summary>
    public int MapPosition(int x)
    {
        if (_insert)
        {
            if (x < _start) return x;
            long moved = (long)x + _count;
            return moved > _bound ? -1 : (int)moved;
        }
        int last = _start + _count - 1;
        if (x < _start) return x;
        return x <= last ? -1 : x - _count;
    }

    /// <summary>The position before the edit of a cell that is at <paramref name="x"/> after it (the inverse move used
    /// by undo). For an insert, positions inside the inserted span have no origin (they are empty after the edit).</summary>
    public int UnmapPosition(int x)
    {
        if (x < _start) return x;
        return _insert ? x - _count : x + _count;
    }

    /// <summary>The new [lo, hi] of a span along the axis (lo ≤ hi), or null when it becomes invalid.</summary>
    public (int Lo, int Hi)? MapSpan(int lo, int hi)
    {
        if (_insert)
        {
            long newLo = lo >= _start ? (long)lo + _count : lo;
            long newHi = hi >= _start ? (long)hi + _count : hi;
            if (newLo > _bound) return null;        // R61: the start passes the bound
            if (newHi > _bound) newHi = _bound;     // R61: the end alone is clamped
            return ((int)newLo, (int)newHi);
        }
        int last = _start + _count - 1;
        if (lo >= _start && hi <= last) return null; // nothing survives
        int mappedLo = lo < _start ? lo : lo > last ? lo - _count : _start;      // first survivor, shifted
        int mappedHi = hi > last ? hi - _count : hi < _start ? hi : _start - 1;  // last survivor, shifted
        return (mappedLo, mappedHi);
    }

    public CellReference? Rewrite(CellReference reference)
    {
        var a = reference.Address;
        int x = _rows ? a.Row : a.Column;
        int mapped = MapPosition(x);
        if (mapped < 0)
        {
            Changed = true;
            return null;
        }
        if (mapped == x) return reference;
        Changed = true;
        return reference with { Address = With(a, mapped) };
    }

    public (CellReference Start, CellReference End)? RewriteRange(CellReference start, CellReference end)
    {
        int s = _rows ? start.Address.Row : start.Address.Column;
        int e = _rows ? end.Address.Row : end.Address.Column;
        bool startIsLo = s <= e;
        var span = MapSpan(Math.Min(s, e), Math.Max(s, e));
        if (span is not { } m)
        {
            Changed = true;
            return null;
        }
        int newS = startIsLo ? m.Lo : m.Hi;
        int newE = startIsLo ? m.Hi : m.Lo;
        if (newS == s && newE == e) return (start, end);
        Changed = true;
        return (start with { Address = With(start.Address, newS) }, end with { Address = With(end.Address, newE) });
    }

    /// <summary>The address with its coordinate along the axis replaced.</summary>
    public CellAddress With(CellAddress a, int position) =>
        _rows ? new CellAddress(a.Column, position) : new CellAddress(position, a.Row);

    /// <summary>The coordinate of <paramref name="a"/> along the axis.</summary>
    public int PositionOf(CellAddress a) => _rows ? a.Row : a.Column;
}
