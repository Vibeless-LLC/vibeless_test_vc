using Gridline.Core.Addressing;

namespace Gridline.Core.Evaluation;

/// <summary>One cell of a reference argument: its address, its value, and whether it is truly blank (§7.0).</summary>
public readonly record struct ReferenceValue(CellAddress Address, CellValue Value)
{
    /// <summary>True when the cell is empty (the provider returned Blank).</summary>
    public bool IsBlank => Value.Kind == ValueKind.Blank;
}

/// <summary>
/// The cells of a range in row-major order (top row first, each row left to right), read lazily from an
/// <see cref="ICellValueProvider"/>. A struct with a struct enumerator, so <c>foreach</c> allocates nothing per cell
/// (performance budget P-4, §12). Function tests can construct one directly over any provider.
/// </summary>
public readonly struct ReferenceValues
{
    private readonly ICellValueProvider _provider;

    public ReferenceValues(ICellValueProvider provider, CellRange range)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        Range = range;
    }

    /// <summary>The normalised range being enumerated (a single cell reference is a 1x1 range).</summary>
    public CellRange Range { get; }

    /// <summary>Number of cells in the range.</summary>
    public long Count => Range.CellCount;

    /// <summary>Number of rows in the range.</summary>
    public int Rows => Range.BottomRight.Row - Range.TopLeft.Row + 1;

    /// <summary>Number of columns in the range.</summary>
    public int Columns => Range.BottomRight.Column - Range.TopLeft.Column + 1;

    /// <summary>The value at a 0-based offset from the range's top-left cell (random access, for lookups). Offsets are not
    /// limited to the range: they are read relative to the top-left cell (SUMIF's resized sum_range, D-45); the caller keeps the address inside the grid.</summary>
    public CellValue ValueAt(int rowOffset, int columnOffset) =>
        _provider.GetValue(new CellAddress(Range.TopLeft.Column + columnOffset, Range.TopLeft.Row + rowOffset));

    public Enumerator GetEnumerator() => new(_provider, Range);

    /// <summary>Row-major enumerator over <see cref="ReferenceValues"/>.</summary>
    public struct Enumerator
    {
        private readonly ICellValueProvider _provider;
        private readonly int _left, _right, _bottom;
        private int _col, _row;
        private ReferenceValue _current;

        internal Enumerator(ICellValueProvider provider, CellRange range)
        {
            _provider = provider;
            _left = range.TopLeft.Column;
            _right = range.BottomRight.Column;
            _bottom = range.BottomRight.Row;
            _col = _left - 1;
            _row = range.TopLeft.Row;
            _current = default;
        }

        public readonly ReferenceValue Current => _current;

        public bool MoveNext()
        {
            if (_provider is null || _row > _bottom) return false;
            if (++_col > _right)
            {
                _col = _left;
                if (++_row > _bottom) return false;
            }
            var address = new CellAddress(_col, _row);
            _current = new ReferenceValue(address, _provider.GetValue(address));
            return true;
        }
    }
}
