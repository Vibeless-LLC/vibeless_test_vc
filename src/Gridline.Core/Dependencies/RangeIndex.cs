using System.Numerics;
using System.Runtime.CompilerServices;
using Gridline.Core.Addressing;

namespace Gridline.Core.Dependencies;

/// <summary>
/// Reverse index from cells to the formulas that reference them through a range (§6.1). Each distinct range is
/// stored once with the set of formulas that use it (reference-counted by that set). Ranges are bucketed in a
/// multi-level grid: a range of width w and height h goes to level (i, j) with 2^i the smallest power of two >= w and
/// 2^j the smallest >= h, and is registered in the at most 2 x 2 tiles of size 2^i x 2^j it overlaps.
/// <para>Insert and remove are O(1) amortised: each entry records its position in each of its (at most four) buckets,
/// so removal swap-removes by position and fixes the moved entry's recorded position, with no bucket scan (a bucket
/// can hold tens of thousands of entries, e.g. running totals <c>SUM($A$1:A_r)</c> all share tile 0 of their level).</para>
/// <para>A lookup visits the non-empty levels (at most 11 x 18 = 198), skips a level after four compares when the cell
/// lies outside the bounding box of that level's ranges, otherwise checks one tile and filters its candidates by
/// containment. So lookup is O(L + K): L non-empty levels and K candidate ranges in the cell's tiles, independent of
/// the total number of ranges.</para>
/// </summary>
internal sealed class RangeIndex
{
    private const int ColumnLevels = 11; // 2^10 = 1024 >= 702 columns
    private const int RowLevels = 18;    // 2^17 = 131072 >= 100,000 rows

    /// <summary>An entry's position in each of its buckets, indexed by <see cref="Slot"/>.</summary>
    [InlineArray(4)]
    internal struct BucketPositions
    {
        private int _element;
    }

    internal sealed class Entry(CellRange range)
    {
        public CellRange Range { get; } = range;
        public HashSet<long> Dependents { get; } = [];
        // Zero-based bounds, cached for the lookup loop.
        public readonly int Left = range.TopLeft.Column - 1, Top = range.TopLeft.Row - 1;
        public readonly int Right = range.BottomRight.Column - 1, Bottom = range.BottomRight.Row - 1;
        public BucketPositions Positions;
    }

    /// <summary>Bounding box (zero-based, inclusive) of the ranges at one level. It only grows while the level is
    /// non-empty, so it is a superset of the level's ranges (correct for skipping), and is reset when the level empties.</summary>
    private struct LevelBounds
    {
        public int Left, Top, Right, Bottom;
    }

    private readonly Dictionary<CellRange, Entry> _entries = [];
    private readonly Dictionary<long, List<Entry>> _buckets = [];
    private readonly int[] _levelCounts = new int[ColumnLevels * RowLevels];
    private readonly LevelBounds[] _levelBounds = new LevelBounds[ColumnLevels * RowLevels];
    private int[] _activeLevels = [];

    public int RangeCount => _entries.Count;

    public void Add(CellRange range, long dependent)
    {
        if (!_entries.TryGetValue(range, out var entry))
        {
            entry = new Entry(range);
            _entries.Add(range, entry);
            int level = LevelOf(entry);
            int i = level / RowLevels, j = level % RowLevels;
            for (int tc = entry.Left >> i; tc <= entry.Right >> i; tc++)
            {
                for (int tr = entry.Top >> j; tr <= entry.Bottom >> j; tr++)
                {
                    long key = Key(level, tc, tr);
                    if (!_buckets.TryGetValue(key, out var bucket)) _buckets.Add(key, bucket = []);
                    entry.Positions[Slot(entry, i, j, tc, tr)] = bucket.Count;
                    bucket.Add(entry);
                }
            }
            ref var b = ref _levelBounds[level];
            if (_levelCounts[level]++ == 0)
            {
                b = new LevelBounds { Left = entry.Left, Top = entry.Top, Right = entry.Right, Bottom = entry.Bottom };
                RebuildActiveLevels();
            }
            else
            {
                b.Left = Math.Min(b.Left, entry.Left);
                b.Top = Math.Min(b.Top, entry.Top);
                b.Right = Math.Max(b.Right, entry.Right);
                b.Bottom = Math.Max(b.Bottom, entry.Bottom);
            }
        }
        entry.Dependents.Add(dependent);
    }

    public void Remove(CellRange range, long dependent)
    {
        if (!_entries.TryGetValue(range, out var entry) || !entry.Dependents.Remove(dependent)) return;
        if (entry.Dependents.Count > 0) return;
        _entries.Remove(range);
        int level = LevelOf(entry);
        int i = level / RowLevels, j = level % RowLevels;
        for (int tc = entry.Left >> i; tc <= entry.Right >> i; tc++)
        {
            for (int tr = entry.Top >> j; tr <= entry.Bottom >> j; tr++)
            {
                long key = Key(level, tc, tr);
                var bucket = _buckets[key];
                int at = entry.Positions[Slot(entry, i, j, tc, tr)];
                int lastAt = bucket.Count - 1;
                if (at != lastAt)
                {
                    var moved = bucket[lastAt];
                    bucket[at] = moved;
                    moved.Positions[Slot(moved, i, j, tc, tr)] = at; // same level and tile, so the same slot formula
                }
                bucket.RemoveAt(lastAt);
                if (lastAt == 0) _buckets.Remove(key);
            }
        }
        if (--_levelCounts[level] == 0) RebuildActiveLevels();
    }

    /// <summary>Appends every formula that references a range containing the cell (duplicates possible across ranges).</summary>
    public void CollectDependents(int column, int row, List<long> output)
    {
        int c = column - 1, r = row - 1;
        foreach (int level in _activeLevels)
        {
            ref readonly var b = ref _levelBounds[level];
            if (c < b.Left || c > b.Right || r < b.Top || r > b.Bottom) continue;
            int i = level / RowLevels, j = level % RowLevels;
            if (!_buckets.TryGetValue(Key(level, c >> i, r >> j), out var bucket)) continue;
            foreach (var e in bucket)
            {
                if (c < e.Left || c > e.Right || r < e.Top || r > e.Bottom) continue;
                foreach (long d in e.Dependents) output.Add(d);
            }
        }
    }

    private static int LevelOf(Entry e)
    {
        int i = CeilLog2(e.Right - e.Left + 1), j = CeilLog2(e.Bottom - e.Top + 1);
        return i * RowLevels + j;
    }

    private static int CeilLog2(int n) => n <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(n - 1));

    private static long Key(int level, int tileColumn, int tileRow) =>
        ((long)level << 32) | ((long)tileColumn << 17) | (uint)tileRow;

    /// <summary>The index (0..3) of tile (tileColumn, tileRow) among the entry's tiles at level (i, j); a range at that
    /// level spans at most two tiles in each direction because its width is at most 2^i and its height at most 2^j.</summary>
    private static int Slot(Entry e, int i, int j, int tileColumn, int tileRow) =>
        ((tileColumn - (e.Left >> i)) << 1) | (tileRow - (e.Top >> j));

    private void RebuildActiveLevels()
    {
        var active = new List<int>();
        for (int level = 0; level < _levelCounts.Length; level++)
            if (_levelCounts[level] > 0) active.Add(level);
        _activeLevels = [.. active];
    }
}
