using Gridline.Core.Evaluation;

namespace Gridline.Core.Functions;

/// <summary>
/// The lookup and reference functions of §7.4 (rulings R65-R67, D-44). Registered by <see cref="Register"/>.
/// </summary>
/// <remarks>
/// Range parameters (INDEX range, MATCH range, VLOOKUP/HLOOKUP table, ROWS, COLUMNS) go through <see cref="TryGrid"/>:
/// a name that cannot be a reference gives its own error (#NAME?/#REF!, D-41) before anything else, a reference is read
/// lazily by cell, and a direct scalar is a 1x1 range (R65). Errors in arguments propagate in argument order (§6.4).
/// Integer parameters truncate toward zero (§6.9). Exact-mode equality (R66): number = number, boolean = boolean, text
/// case-insensitive with wildcards (<see cref="WildcardMatcher"/>); blank keys never match; a blank lookup value is 0.
/// Approximate mode and MATCH 1/-1 order with <see cref="ValueComparer"/> (§6.3); an error cell among the keys counts as
/// out of order (never selected), since results on unsorted columns are unspecified.
/// </remarks>
internal static class LookupFunctions
{
    private static readonly CellValue ValueError = CellValue.FromError(ErrorCodes.Value);
    private static readonly CellValue RefError = CellValue.FromError(ErrorCodes.Ref);
    private static readonly CellValue NAError = CellValue.FromError(ErrorCodes.NA);
    private static readonly CellValue Zero = CellValue.FromNumber(0);

    public static void Register(BuiltInFunctionTable.Builder b)
    {
        Add(b, "VLOOKUP", 3, 4, a => TableLookup(a, byColumn: true));
        Add(b, "HLOOKUP", 3, 4, a => TableLookup(a, byColumn: false));
        Add(b, "INDEX", 2, 3, Index);
        Add(b, "MATCH", 2, 3, Match);
        Add(b, "CHOOSE", 2, BuiltInFunctionTable.MaxVariadicArguments, Choose);
        Add(b, "ROW", 0, 1, a => Position(a, row: true));
        Add(b, "COLUMN", 0, 1, a => Position(a, row: false));
        Add(b, "ROWS", 1, 1, a => Size(a, rows: true));
        Add(b, "COLUMNS", 1, 1, a => Size(a, rows: false));
    }

    private static void Add(BuiltInFunctionTable.Builder b, string name, int min, int max, Func<IFunctionArguments, CellValue> body) =>
        b.Add(name, min, max, new DelegateFunction(body));

    // ---- argument helpers ----

    /// <summary>A rectangular lookup range: a reference read lazily, or a direct scalar as a 1x1 range (R65).</summary>
    private readonly struct Grid
    {
        private readonly ReferenceValues _refs;
        private readonly bool _isRef;
        private readonly CellValue _scalar;

        public Grid(ReferenceValues refs) { _refs = refs; _isRef = true; _scalar = default; Rows = refs.Rows; Columns = refs.Columns; }
        public Grid(CellValue scalar) { _refs = default; _isRef = false; _scalar = scalar; Rows = 1; Columns = 1; }

        public int Rows { get; }
        public int Columns { get; }
        public CellValue At(int row, int column) => _isRef ? _refs.ValueAt(row, column) : _scalar;
    }

    private static bool TryGrid(IFunctionArguments a, int index, out Grid grid, out CellValue error)
    {
        grid = default;
        if (a.TryGetReferenceError(index, out error)) return false;      // D-41: #NAME? / #REF! before any #VALUE!
        if (a.IsReference(index))
        {
            grid = new Grid(a.EnumerateReferenceValues(index));
            return true;
        }
        var v = a.Evaluate(index);
        if (v.Kind == ValueKind.Error) { error = v; return false; }
        grid = new Grid(v);
        return true;
    }

    private static bool Given(IFunctionArguments a, int index) => index < a.Count && !a.IsOmitted(index);

    private static bool TryInt(IFunctionArguments a, int index, out long value, out CellValue error)
    {
        value = 0;
        if (!Coercion.TryToNumber(a.Evaluate(index), out var n, out error)) return false;
        value = (long)Math.Clamp(Math.Truncate(n), -4e9, 4e9);
        return true;
    }

    /// <summary>The lookup value: evaluated as a scalar, a blank is the number 0 (R66, §6.6); errors propagate.</summary>
    private static bool TryLookupValue(IFunctionArguments a, int index, out CellValue value)
    {
        value = a.Evaluate(index);
        if (value.Kind == ValueKind.Blank) value = Zero;
        return value.Kind != ValueKind.Error;
    }

    // ---- matching ----

    /// <summary>Exact-mode equality (R66); <paramref name="pattern"/> is the upper-cased lookup text when it is text.</summary>
    private static bool ExactEquals(CellValue key, CellValue v, string? pattern) => v.Kind switch
    {
        ValueKind.Number => key.Kind == ValueKind.Number && key.Number == v.Number,
        ValueKind.Boolean => key.Kind == ValueKind.Boolean && key.Boolean == v.Boolean,
        ValueKind.Text => key.Kind == ValueKind.Text && WildcardMatcher.IsMatch(pattern!, key.Text.ToUpperInvariant()),
        _ => false,
    };

    /// <summary>First position (0-based) of <paramref name="v"/> among <paramref name="count"/> keys, or -1.</summary>
    private static int FindExact(Func<int, CellValue> key, int count, CellValue v)
    {
        string? pattern = v.Kind == ValueKind.Text ? v.Text.ToUpperInvariant() : null;
        for (int i = 0; i < count; i++)
            if (ExactEquals(key(i), v, pattern)) return i;
        return -1;
    }

    /// <summary>Binary search: the last position whose key is key &lt;= v (<paramref name="descending"/> false) or key &gt;= v
    /// (true), assuming the keys are sorted that way. -1 when none.</summary>
    private static int FindApproximate(Func<int, CellValue> key, int count, CellValue v, bool descending)
    {
        int lo = 0, hi = count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            var k = key(mid);
            bool qualifies = k.Kind != ValueKind.Error &&
                (descending ? ValueComparer.Compare(k, v) >= 0 : ValueComparer.Compare(k, v) <= 0);
            if (qualifies) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    // ---- VLOOKUP / HLOOKUP ----

    private static CellValue TableLookup(IFunctionArguments a, bool byColumn)
    {
        if (!TryLookupValue(a, 0, out var v)) return v;
        if (!TryGrid(a, 1, out var table, out var err)) return err;
        if (!TryInt(a, 2, out var offset, out err)) return err;
        bool approximate = true;                          // default TRUE; an omitted fourth argument is absent (R38)
        if (Given(a, 3))
        {
            var flag = Coercion.ToBoolean(a.Evaluate(3));
            if (flag.Kind == ValueKind.Error) return flag;
            approximate = flag.Boolean;
        }

        long extent = byColumn ? table.Columns : table.Rows;    // size across the keys
        if (offset < 1) return ValueError;
        if (offset > extent) return RefError;

        int count = byColumn ? table.Rows : table.Columns;
        Func<int, CellValue> key = byColumn ? i => table.At(i, 0) : i => table.At(0, i);
        int at = approximate ? FindApproximate(key, count, v, descending: false) : FindExact(key, count, v);
        if (at < 0) return NAError;
        return byColumn ? table.At(at, (int)offset - 1) : table.At((int)offset - 1, at);
    }

    // ---- INDEX ----

    private static CellValue Index(IFunctionArguments a)
    {
        if (!TryGrid(a, 0, out var grid, out var err)) return err;
        if (!TryInt(a, 1, out var r, out err)) return err;
        long c = 0;
        bool hasColumn = Given(a, 2);
        if (hasColumn && !TryInt(a, 2, out c, out err)) return err;

        if (r <= 0 || (hasColumn && c <= 0)) return ValueError;          // zero and, R65, negative indices
        if (hasColumn)
            return r > grid.Rows || c > grid.Columns ? RefError : grid.At((int)r - 1, (int)c - 1);

        // One index: it selects along the single row or column (a 1x1 range counts as a column).
        if (grid.Columns == 1) return r > grid.Rows ? RefError : grid.At((int)r - 1, 0);
        if (grid.Rows == 1) return r > grid.Columns ? RefError : grid.At(0, (int)r - 1);
        return ValueError;                                               // 2-D range without a column
    }

    // ---- MATCH ----

    private static CellValue Match(IFunctionArguments a)
    {
        if (!TryLookupValue(a, 0, out var v)) return v;
        if (!TryGrid(a, 1, out var grid, out var err)) return err;
        long type = 1;
        if (Given(a, 2) && !TryInt(a, 2, out type, out err)) return err;
        if (grid.Rows != 1 && grid.Columns != 1) return NAError;

        bool column = grid.Columns == 1;
        int count = column ? grid.Rows : grid.Columns;
        Func<int, CellValue> key = column ? i => grid.At(i, 0) : i => grid.At(0, i);
        int at = type == 0 ? FindExact(key, count, v) : FindApproximate(key, count, v, descending: type < 0);
        return at < 0 ? NAError : CellValue.FromNumber(at + 1);
    }

    // ---- CHOOSE ----

    private static CellValue Choose(IFunctionArguments a)
    {
        if (!TryInt(a, 0, out var i, out var err)) return err;
        if (i < 1 || i > a.Count - 1) return ValueError;
        int chosen = (int)i;
        if (a.IsOmitted(chosen)) return Zero;           // as IF's omitted true branch (R67, R38)
        return a.Evaluate(chosen);                       // only the chosen argument is evaluated, a range as in IF (R67)
    }

    // ---- ROW / COLUMN / ROWS / COLUMNS ----

    private static CellValue Position(IFunctionArguments a, bool row)
    {
        if (a.Count == 0 || a.IsOmitted(0))
        {
            if (a.CurrentCell is not { } cell) return ValueError;
            return CellValue.FromNumber(row ? cell.Row : cell.Column);
        }
        if (a.TryGetReferenceError(0, out var error)) return error;
        if (!a.IsReference(0)) return ValueError;       // R65: a direct argument
        var top = a.EnumerateReferenceValues(0).Range.TopLeft;
        return CellValue.FromNumber(row ? top.Row : top.Column);
    }

    private static CellValue Size(IFunctionArguments a, bool rows)
    {
        if (!TryGrid(a, 0, out var grid, out var err)) return err;
        return CellValue.FromNumber(rows ? grid.Rows : grid.Columns);
    }
}
