using System.Numerics;
using Gridline.Core.Addressing;

namespace Gridline.Core.Dependencies;

/// <summary>
/// Reverse index from cells to the formulas that reference them through a range (§6.1). Each distinct range is
/// stored once with the set of formulas that use it. Ranges are bucketed in a multi-level grid: a range of width w and
/// height h goes to level (i, j) with 2^i the smallest power of two >= w and 2^j the smallest >= h, and is
/// registered in the at most 2 x 2 tiles of size 2^i x 2^j it overlaps. A lookup checks one tile per non-empty level
/// (at most 11 x 18 = 198 levels, usually a handful) and filters its candidates with <see cref="CellRange.Contains"/>.
/// So insert/remove touch at most four buckets, and lookup is O(L + K) where L is the number of non-empty levels and
/// K is the number of candidate ranges in the cell's tiles (the ranges containing the cell plus same-sized ranges that
/// start or end within one tile of it), independent of the total number of ranges.
/// </summary>
internal sealed class RangeIndex
{
    private const int ColumnLevels = 11; // 2^10 = 1024 >= 702 columns
    private const int RowLevels = 18;    // 2^17 = 131072 >= 100,000 rows

    internal sealed class Entry(CellRange range)
    {
        public CellRange Range { get; } = range;
        public HashSet<int> Dependents { get; } = [];
        // Zero-based bounds, cached for the lookup loop.
        public readonly int Left = range.TopLeft.Column - 1, Top = range.TopLeft.Row - 1;
        public readonly int Right = range.BottomRight.Column - 1, Bottom = range.BottomRight.Row - 1;
    }

    private readonly Dictionary<CellRange, Entry> _entries = [];
    private readonly Dictionary<long, List<Entry>> _buckets = [];
    private readonly int[] _levelCounts = new int[ColumnLevels * RowLevels];
    private int[] _activeLevels = [];

    public int RangeCount => _entries.Count;

    public void Add(CellRange range, int dependent)
    {
        if (!_entries.TryGetValue(range, out var entry))
        {
            entry = new Entry(range);
            _entries.Add(range, entry);
            int level = LevelOf(entry);
            foreach (long key in TileKeys(entry, level))
            {
                if (!_buckets.TryGetValue(key, out var bucket)) _buckets.Add(key, bucket = []);
                bucket.Add(entry);
            }
            if (_levelCounts[level]++ == 0) RebuildActiveLevels();
        }
        entry.Dependents.Add(dependent);
    }

    public void Remove(CellRange range, int dependent)
    {
        if (!_entries.TryGetValue(range, out var entry) || !entry.Dependents.Remove(dependent)) return;
        if (entry.Dependents.Count > 0) return;
        _entries.Remove(range);
        int level = LevelOf(entry);
        foreach (long key in TileKeys(entry, level))
        {
            var bucket = _buckets[key];
            int at = bucket.IndexOf(entry);
            bucket[at] = bucket[^1];
            bucket.RemoveAt(bucket.Count - 1);
            if (bucket.Count == 0) _buckets.Remove(key);
        }
        if (--_levelCounts[level] == 0) RebuildActiveLevels();
    }

    /// <summary>Appends every formula that references a range containing the cell (duplicates possible across ranges).</summary>
    public void CollectDependents(int column, int row, List<int> output)
    {
        int c = column - 1, r = row - 1;
        foreach (int level in _activeLevels)
        {
            int i = level / RowLevels, j = level % RowLevels;
            if (!_buckets.TryGetValue(Key(level, c >> i, r >> j), out var bucket)) continue;
            foreach (var e in bucket)
            {
                if (c < e.Left || c > e.Right || r < e.Top || r > e.Bottom) continue;
                foreach (int d in e.Dependents) output.Add(d);
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

    private static IEnumerable<long> TileKeys(Entry e, int level)
    {
        int i = level / RowLevels, j = level % RowLevels;
        for (int tc = e.Left >> i; tc <= e.Right >> i; tc++)
            for (int tr = e.Top >> j; tr <= e.Bottom >> j; tr++)
                yield return Key(level, tc, tr);
    }

    private void RebuildActiveLevels()
    {
        var active = new List<int>();
        for (int level = 0; level < _levelCounts.Length; level++)
            if (_levelCounts[level] > 0) active.Add(level);
        _activeLevels = [.. active];
    }
}
