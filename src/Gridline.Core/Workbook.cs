using System.Diagnostics.CodeAnalysis;
using Gridline.Core.Addressing;
using Gridline.Core.Dependencies;
using Gridline.Core.Evaluation;
using Gridline.Core.Functions;
using Gridline.Core.Parsing;

namespace Gridline.Core;

/// <summary>Statistics of the most recent recalculation (§9.1, R21, R23).</summary>
/// <param name="EvaluatedFormulaCount">Formula cells in the affected set (the edited formula plus transitive dependents), #CIRC! cells included.</param>
public readonly record struct RecalcStats(int EvaluatedFormulaCount);

/// <summary>Arguments of <see cref="Workbook.CellsChanged"/> (§9.1, R23).</summary>
public sealed class CellsChangedEventArgs : EventArgs
{
    /// <summary>Wraps <paramref name="addresses"/> read-only (§9.1). A <c>string[]</c> is wrapped without copying (the caller must not mutate it afterwards); any other list is copied.</summary>
    public CellsChangedEventArgs(IReadOnlyList<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        Addresses = Array.AsReadOnly(addresses as string[] ?? [.. addresses]);
    }

    /// <summary>Canonical addresses of every cell whose input or value changed, row-major, each once. Read-only: casting to <see cref="IList{T}"/> yields a read-only list.</summary>
    public IReadOnlyList<string> Addresses { get; }
}

/// <summary>
/// The spreadsheet engine facade (§9.1, §9.2): composes addressing, §4.3 input interpretation, the parser, the
/// evaluator, the built-in function table and the dependency engine. Every mutating call recalculates synchronously,
/// raises <see cref="CellsChanged"/> at most once afterwards, and is atomic: if it throws, the workbook is unchanged.
/// Not thread-safe; use from one thread only.
/// </summary>
public sealed class Workbook
{
    public const int MaxRows = GridBounds.MaxRows;
    public const int MaxColumns = GridBounds.MaxColumns;
    public const int MaxInputLength = CellInputInterpreter.MaxInputLength;
    public const int UndoLimit = 100;

    private static readonly FunctionArityAdapter Arities = new(BuiltInFunctionTable.Default);

    /// <summary>One non-empty cell. <see cref="Value"/> is the constant value, or the last computed formula value.</summary>
    private sealed class Entry(string input, ParsedFormula? formula, CellValue value)
    {
        public readonly string Input = input;
        public readonly ParsedFormula? Formula = formula;
        public CellValue Value = value;
    }

    private readonly Dictionary<int, Entry> _cells = []; // key: CellKey.Pack
    private readonly DependencyGraph _graph = new();
    private readonly IFunctionResolver _functions;
    private readonly RecalcTarget _target;

    // Per-call scratch, reused so an edit allocates no whole-workbook structures (P-3).
    private readonly CellAddress[] _changedOne = new CellAddress[1];
    private const int UndoRetainCapacity = 1024; // entries; a larger log (huge recalc) is dropped, not retained
    private List<(Entry Entry, CellValue OldValue)> _undo = [];
    private bool _logWrites;

    public Workbook() : this(BuiltInFunctionTable.Default) { }

    /// <summary>Test seam: evaluates with <paramref name="functions"/> instead of the built-in table (parsing still uses built-in arities).</summary>
    internal Workbook(IFunctionResolver functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        _functions = functions;
        _target = new RecalcTarget(this);
    }

    /// <summary>The statistics of the last recalculation; 0 evaluated after a no-op SetCell (R24).</summary>
    public RecalcStats LastRecalc { get; private set; }

    /// <summary>Raised once after a mutating call's recalculation, only when some cell's input or value changed (§9.2).</summary>
    public event EventHandler<CellsChangedEventArgs>? CellsChanged;

    /// <summary>
    /// Builds a workbook from (address, input) pairs in one step (R5): every entry is validated first, then the cells
    /// are stored and one full recalculation runs. Empty inputs are skipped. No <see cref="CellsChanged"/> is raised.
    /// </summary>
    /// <exception cref="ArgumentException">An address or input is null or invalid, an input is too long, or two
    /// entries name the same cell (compared canonically, so "a1" and "$A$1" duplicate "A1"). Nothing is built.</exception>
    public static Workbook FromCells(IEnumerable<KeyValuePair<string, string>> cells) =>
        FromCells(cells, BuiltInFunctionTable.Default);

    internal static Workbook FromCells(IEnumerable<KeyValuePair<string, string>> cells, IFunctionResolver functions)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var validated = new List<(int Key, string Input)>();
        var seen = new HashSet<int>();
        foreach (var (address, input) in cells)
        {
            int key = CellKey.Pack(ParseAddress(address, nameof(cells)));
            ValidateInput(input, nameof(cells));
            if (!seen.Add(key))
                throw new ArgumentException($"Duplicate cell address '{address}'.", nameof(cells));
            if (input.Length != 0) validated.Add((key, input));
        }

        var wb = new Workbook(functions);
        foreach (var (key, input) in validated)
        {
            var entry = CreateEntry(input, CellValue.Blank);
            wb._cells.Add(key, entry);
            if (entry.Formula is not null) wb._graph.SetFormula(CellKey.Unpack(key), entry.Formula);
        }
        var result = wb._graph.RecalculateAll(wb._target, functions);
        wb.LastRecalc = new RecalcStats(result.EvaluatedFormulaCount);
        return wb;
    }

    /// <summary>
    /// Sets a cell's input (§4.3, §9.1) and recalculates. "" clears the cell; input identical to the current input
    /// is a complete no-op (R24). A formula that fails to parse is stored and evaluates to #PARSE!.
    /// </summary>
    /// <exception cref="ArgumentException">The address is null, invalid or a range, or the input is null or longer
    /// than <see cref="MaxInputLength"/>. The workbook is unchanged.</exception>
    public void SetCell(string address, string input)
    {
        var cell = ParseAddress(address, nameof(address));
        ValidateInput(input, nameof(input));
        int key = CellKey.Pack(cell);
        _cells.TryGetValue(key, out var old);
        if (string.Equals(old?.Input ?? "", input, StringComparison.Ordinal))
        {
            LastRecalc = default; // R24
            return;
        }

        // Pure; nothing is mutated yet. A formula keeps the cell's old value until recalc writes the new one, which is
        // what RecalcResult.ChangedCells compares against (RecalcResult contract).
        var entry = input.Length == 0 ? null : CreateEntry(input, old?.Value ?? CellValue.Blank);
        bool edgesChanged = old?.Formula is not null || entry?.Formula is not null;

        RecalcResult result;
        _undo.Clear();
        _logWrites = true;
        try
        {
            if (entry is null) _cells.Remove(key);
            else _cells[key] = entry;
            if (edgesChanged) _graph.SetFormula(cell, entry?.Formula);
            _changedOne[0] = cell;
            result = _graph.Recalculate(_changedOne, _target, _functions);
        }
        catch
        {
            for (int i = _undo.Count - 1; i >= 0; i--) _undo[i].Entry.Value = _undo[i].OldValue;
            if (old is null) _cells.Remove(key);
            else _cells[key] = old;
            if (edgesChanged) _graph.SetFormula(cell, old?.Formula);
            throw;
        }
        finally
        {
            _logWrites = false;
            if (_undo.Capacity > UndoRetainCapacity) _undo = []; // release the big backing array
            else _undo.Clear();
        }

        LastRecalc = new RecalcStats(result.EvaluatedFormulaCount);
        var handler = CellsChanged;
        if (handler is not null) handler(this, new CellsChangedEventArgs(ChangedAddresses(key, result.ChangedCells)));
    }

    /// <summary>The cell's input as entered; "" for an empty cell.</summary>
    /// <exception cref="ArgumentException">The address is null, invalid or a range.</exception>
    public string GetInput(string address) =>
        _cells.TryGetValue(CellKey.Pack(ParseAddress(address, nameof(address))), out var e) ? e.Input : "";

    /// <summary>The cell's current value; <see cref="CellValue.Blank"/> for an empty cell.</summary>
    /// <exception cref="ArgumentException">The address is null, invalid or a range.</exception>
    public CellValue GetValue(string address) =>
        _cells.TryGetValue(CellKey.Pack(ParseAddress(address, nameof(address))), out var e) ? e.Value : CellValue.Blank;

    /// <summary>Canonical addresses of every non-empty cell, row-major (row, then column).</summary>
    public IReadOnlyList<string> GetUsedAddresses()
    {
        var keys = new int[_cells.Count];
        _cells.Keys.CopyTo(keys, 0);
        return Array.AsReadOnly(ToSortedAddresses(keys));
    }

    private static CellAddress ParseAddress(string? address, string paramName)
    {
        ArgumentNullException.ThrowIfNull(address, paramName);
        if (!CellAddress.TryParse(address, out var cell))
            throw new ArgumentException($"Invalid cell address: '{address}'.", paramName);
        return cell;
    }

    private static void ValidateInput(string? input, string paramName)
    {
        ArgumentNullException.ThrowIfNull(input, paramName);
        if (input.Length > MaxInputLength)
            throw new ArgumentException($"Input exceeds {MaxInputLength} UTF-16 code units.", paramName);
    }

    /// <summary>Interprets non-empty input (§4.3). A formula holds <paramref name="formulaValue"/> until recalc computes it.</summary>
    private static Entry CreateEntry(string input, CellValue formulaValue)
    {
        var interpreted = CellInputInterpreter.Interpret(input);
        return interpreted.Kind == CellInputKind.Formula
            ? new Entry(input, FormulaParser.Parse(interpreted.FormulaText, Arities), formulaValue)
            : new Entry(input, null, interpreted.Value);
    }

    /// <summary>The edited cell (its input always changed) plus the formula cells whose value changed.</summary>
    private static string[] ChangedAddresses(int editedKey, IReadOnlyList<CellAddress> changedFormulas)
    {
        if (changedFormulas.Count == 0) return [CellKey.Unpack(editedKey).ToString()];
        var keys = new int[changedFormulas.Count + 1];
        keys[0] = editedKey;
        for (int i = 0; i < changedFormulas.Count; i++) keys[i + 1] = CellKey.Pack(changedFormulas[i]);
        return ToSortedAddresses(keys);
    }

    /// <summary>Sorts packed keys row-major (projected key <c>row &lt;&lt; 10 | column</c>, column &lt; 1024), drops duplicates and formats them canonically. Reuses <paramref name="keys"/> as the sort buffer.</summary>
    private static string[] ToSortedAddresses(int[] keys)
    {
        for (int i = 0; i < keys.Length; i++) keys[i] = (CellKey.Row(keys[i]) << 10) | CellKey.Column(keys[i]);
        Array.Sort(keys);
        int unique = 0;
        for (int i = 0; i < keys.Length; i++)
            if (i == 0 || keys[i] != keys[i - 1]) unique++;
        var result = new string[unique];
        int n = 0;
        for (int i = 0; i < keys.Length; i++)
            if (i == 0 || keys[i] != keys[i - 1])
                result[n++] = new CellAddress(keys[i] & 1023, keys[i] >> 10).ToString();
        return result;
    }

    /// <summary>The store the dependency graph recalculates against; kept private so the public surface stays §9.1.</summary>
    private sealed class RecalcTarget(Workbook owner) : IRecalcTarget
    {
        public CellValue GetValue(CellAddress address) =>
            owner._cells.TryGetValue(CellKey.Pack(address), out var e) ? e.Value : CellValue.Blank;

        public bool TryGetFormula(CellAddress address, [NotNullWhen(true)] out ParsedFormula? formula)
        {
            formula = owner._cells.TryGetValue(CellKey.Pack(address), out var e) ? e.Formula : null;
            return formula is not null;
        }

        public void SetComputedValue(CellAddress address, CellValue value)
        {
            var entry = owner._cells[CellKey.Pack(address)];
            if (owner._logWrites) owner._undo.Add((entry, entry.Value));
            entry.Value = value;
        }
    }
}
