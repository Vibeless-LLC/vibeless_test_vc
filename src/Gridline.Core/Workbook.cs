using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Gridline.Core.Addressing;
using Gridline.Core.Commands;
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

    /// <summary>Canonical addresses of every cell whose input or value changed, each once (§9.1: declared as
    /// <see cref="IReadOnlyCollection{T}"/>). With one sheet they are unqualified and row-major; with several they are
    /// qualified, in sheet order then row-major (R81). Read-only: casting to <see cref="IList{T}"/> yields a read-only list.</summary>
    public IReadOnlyCollection<string> Addresses { get; }
}

/// <summary>
/// The spreadsheet engine facade (§9.1, §9.2): composes addressing, §4.3 input interpretation, the parser, the
/// evaluator, the built-in function table and the dependency engine. Every mutating call recalculates synchronously,
/// raises <see cref="CellsChanged"/> at most once afterwards, and is atomic: if it throws, the workbook is unchanged.
/// A workbook holds an ordered list of 1 to 255 sheets, exactly one of them active (CR1.1, CR1.3); a new workbook has
/// one sheet, Sheet1. Not thread-safe; use from one thread only.
/// </summary>
public sealed partial class Workbook
{
    public const int MaxRows = GridBounds.MaxRows;
    public const int MaxColumns = GridBounds.MaxColumns;
    public const int MaxInputLength = CellInputInterpreter.MaxInputLength;
    public const int UndoLimit = 100;

    /// <summary>The most sheets a workbook holds (CR1.1, R78).</summary>
    internal const int MaxSheetCount = 255;

    /// <summary>The name of the sheet of a new or single-sheet-loaded workbook (CR1.1).</summary>
    internal const string DefaultSheetName = "Sheet1";

    private static readonly FunctionArityAdapter Arities = new(BuiltInFunctionTable.Default);

    /// <summary>One non-empty cell. <see cref="Value"/> is the constant value, or the last computed formula value.</summary>
    private sealed class Entry(string input, ParsedFormula? formula, CellValue value)
    {
        public readonly string Input = input;
        public readonly ParsedFormula? Formula = formula;
        public CellValue Value = value;
    }

    /// <summary>
    /// One sheet (CR1.1, D-52): a stable internal <see cref="Id"/> that never changes (so a rename or move never re-keys
    /// cells, and undo steps refer to sheets by id), its defined name, and its cell store keyed by <see cref="CellKey"/>.
    /// </summary>
    private sealed class Sheet
    {
        public Sheet(Workbook owner, int id, string name)
        {
            Id = id;
            Name = name;
            Prefix = SheetName.Format(name) + "!";
            Target = new SheetTarget(owner, this);
        }

        public readonly int Id;
        public string Name { get; private set; }

        /// <summary>The qualified-address prefix: the name quoted only when required (R82), then '!'.</summary>
        public string Prefix { get; private set; }

        /// <summary>RenameSheet (CR1.3, D-54): the id and cells stay; only the name and prefix change. The caller keeps
        /// the workbook's name lookup and the graph's sheet binding in step.</summary>
        public void SetName(string name)
        {
            Name = name;
            Prefix = SheetName.Format(name) + "!";
        }

        // Not readonly: a structure edit (§9.5) builds the moved cell store aside and swaps it in, so a failure restores
        // the previous object (Workbook.Structure.cs).
        public Dictionary<int, Entry> Cells = []; // key: CellKey.Pack

        /// <summary>The recalc target and value provider of this sheet (evaluation reads unqualified references here).</summary>
        public readonly SheetTarget Target;
    }

    private readonly List<Sheet> _sheets = []; // in order (CR1.1)
    private readonly Dictionary<int, Sheet> _sheetsById = [];
    private readonly Dictionary<string, Sheet> _sheetsByName = new(StringComparer.OrdinalIgnoreCase); // R78: case-insensitive
    private Sheet _active;
    private int _nextSheetId; // stable ids are never reused, except by redo of the step that allocated one

    // Not readonly: a structure edit (§9.5) builds its graph aside and swaps it in (Workbook.Structure.cs).
    private DependencyGraph _graph = new();
    private readonly IFunctionResolver _functions;
    private readonly RecalcRoot _target;
    private readonly NameCasing _nameCasing; // §5.5: rewritten formulas print names in their definition casing
    private readonly SheetCasing _sheetCasing; // R82: rewritten formulas print sheets in their defined casing

    // Per-call scratch, reused so an edit allocates no whole-workbook structures (P-3).
    private readonly long[] _changedOne = new long[1];
    private const int RollbackRetainCapacity = 1024; // entries; a larger log (huge recalc) is dropped, not retained
    private List<(Entry Entry, CellValue OldValue)> _rollback = []; // values written by the running recalc (§9.2)
    private bool _logWrites;
    private readonly UndoHistory _history = new(UndoLimit); // §9.3

    public Workbook() : this(BuiltInFunctionTable.Default) { }

    /// <summary>Test seam: evaluates with <paramref name="functions"/> instead of the built-in table (parsing still uses built-in arities).</summary>
    internal Workbook(IFunctionResolver functions) : this(functions, [DefaultSheetName]) { }

    /// <summary>A workbook with empty sheets named <paramref name="sheetNames"/> (validated by the caller), in order, ids
    /// 0, 1, ...; the first is active.</summary>
    private Workbook(IFunctionResolver functions, IReadOnlyList<string> sheetNames)
    {
        ArgumentNullException.ThrowIfNull(functions);
        _functions = functions;
        _target = new RecalcRoot(this);
        _nameCasing = new NameCasing(this);
        _sheetCasing = new SheetCasing(this);
        foreach (string name in sheetNames) InsertSheet(new Sheet(this, _nextSheetId++, name), _sheets.Count);
        _active = _sheets[0];
    }

    /// <summary>The statistics of the last recalculation; 0 evaluated after a no-op SetCell (R24).</summary>
    public RecalcStats LastRecalc { get; private set; }

    /// <summary>Raised once after a mutating call's recalculation, only when some cell's input or value changed (§9.2).</summary>
    public event EventHandler<CellsChangedEventArgs>? CellsChanged;

    /// <summary>
    /// Builds a single-sheet workbook (Sheet1, CR1.1) from (address, input) pairs in one step (R5): every entry is
    /// validated first, then the cells are stored and one full recalculation runs. Empty inputs are skipped. No
    /// <see cref="CellsChanged"/> is raised; the result is clean with an empty undo history (§8.2, §9.3).
    /// </summary>
    /// <exception cref="ArgumentException">An address or input is null or invalid, an input is too long, or two
    /// entries name the same cell (compared canonically, so "a1" and "$A$1" duplicate "A1"). Nothing is built.</exception>
    public static Workbook FromCells(IEnumerable<KeyValuePair<string, string>> cells) =>
        FromCells(cells, BuiltInFunctionTable.Default);

    internal static Workbook FromCells(IEnumerable<KeyValuePair<string, string>> cells, IFunctionResolver functions) =>
        FromCells(cells, [], functions);

    /// <summary>Internal entry (ruling P1; CSV import and tests): as the public FromCells, plus names in either form
    /// (<see cref="LoadedNameForm.Any"/>). Everything is validated before anything is built (atomic); see
    /// <see cref="ValidateLoadedNames"/>. The .gridline loader calls <see cref="FromSheets"/> directly (D-56).</summary>
    internal static Workbook FromCells(IEnumerable<KeyValuePair<string, string>> cells,
        IEnumerable<KeyValuePair<string, string>> names, IFunctionResolver? functions = null)
    {
        ArgumentNullException.ThrowIfNull(cells);
        return FromSheets([new LoadedSheet(DefaultSheetName, cells)], DefaultSheetName, names, functions);
    }

    /// <summary>
    /// Multi-sheet loader entry (CR1.5, D-52, D-56; the .gridline loader): builds a workbook with <paramref name="sheets"/>
    /// in order, each holding its (unqualified address, input) pairs, and <paramref name="activeSheet"/> active (matched
    /// case-insensitively, R86). Everything is validated before anything is built; then one full recalculation runs. No
    /// event; clean, with an empty undo history. Names are stored qualified (CR1.4, R77): a qualified refersTo must name
    /// one of <paramref name="sheets"/>, and an unqualified one (a version 1 file) is qualified with the first sheet.
    /// <paramref name="nameForm"/> restricts the written form of each refersTo other than <c>#REF!</c>: a version 1
    /// file holds only unqualified targets, a version 2 file only qualified ones (R86, D-56).
    /// </summary>
    /// <exception cref="ArgumentException">No sheets or more than 255, an invalid or duplicate (case-insensitive) sheet
    /// name, an unknown active sheet, an invalid or qualified cell address, a duplicate address within a sheet, an
    /// invalid input, or an invalid name or a refersTo not in <paramref name="nameForm"/>. Nothing is built.</exception>
    internal static Workbook FromSheets(IReadOnlyList<LoadedSheet> sheets, string activeSheet,
        IEnumerable<KeyValuePair<string, string>> names, IFunctionResolver? functions = null,
        LoadedNameForm nameForm = LoadedNameForm.Any)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(activeSheet);
        ArgumentNullException.ThrowIfNull(names);
        functions ??= BuiltInFunctionTable.Default;
        if (sheets.Count is < 1 or > MaxSheetCount)
            throw new ArgumentException($"A workbook has 1 to {MaxSheetCount} sheets, not {sheets.Count}.", nameof(sheets));
        var sheetNames = new string[sheets.Count];
        var seenSheets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int active = -1;
        for (int s = 0; s < sheets.Count; s++)
        {
            string name = sheets[s].Name;
            SheetName.Validate(name, nameof(sheets));
            if (!seenSheets.Add(name)) throw new ArgumentException($"Duplicate sheet name '{name}'.", nameof(sheets));
            if (string.Equals(name, activeSheet, StringComparison.OrdinalIgnoreCase)) active = s;
            sheetNames[s] = name;
        }
        if (active < 0) throw new ArgumentException($"Unknown active sheet '{activeSheet}'.", nameof(activeSheet));
        // The sheets exist before the names are validated, so a qualified refersTo can be checked against them (R77);
        // the workbook is not returned on failure, so nothing escapes (atomic).
        var wb = new Workbook(functions, sheetNames);
        var loadedNames = wb.ValidateLoadedNames(names, nameForm);

        var validated = new List<(int Key, string Input)>[sheets.Count];
        for (int s = 0; s < sheets.Count; s++)
        {
            ArgumentNullException.ThrowIfNull(sheets[s].Cells, nameof(sheets));
            var list = validated[s] = [];
            var seen = new HashSet<int>();
            foreach (var (address, input) in sheets[s].Cells)
            {
                int key = CellKey.Pack(ParseAddress(address, nameof(sheets)));
                ValidateInput(input, nameof(sheets));
                if (!seen.Add(key))
                    throw new ArgumentException($"Duplicate cell address '{address}'.", nameof(sheets));
                if (input.Length != 0) list.Add((key, input));
            }
        }

        wb._active = wb._sheets[active];
        foreach (var definition in loadedNames) wb.SetNameEntry(definition.Name, CreateNameEntry(definition));
        for (int s = 0; s < sheets.Count; s++)
        {
            var sheet = wb._sheets[s];
            foreach (var (key, input) in validated[s])
            {
                var entry = CreateEntry(input, CellValue.Blank);
                sheet.Cells.Add(key, entry);
                if (entry.Formula is not null) wb._graph.SetFormula(sheet.Id, CellKey.Unpack(key), entry.Formula);
            }
        }
        var result = wb._graph.RecalculateAll(wb._target, functions);
        wb.LastRecalc = new RecalcStats(result.EvaluatedFormulaCount);
        return wb;
    }

    /// <summary>
    /// Sets a cell's input (§4.3, §9.1) and recalculates; one undo step (§9.3). "" clears the cell; input identical
    /// to the current input is a complete no-op with no step (R24). A formula that fails to parse is stored and
    /// evaluates to #PARSE!.
    /// </summary>
    /// <param name="address">A cell address, unqualified (the active sheet) or qualified such as <c>Data!A1</c> or
    /// <c>'My Sheet'!$B$2</c> (R83).</param>
    /// <exception cref="ArgumentException">The address is null, invalid, a range or names an unknown sheet, or the input
    /// is null or longer than <see cref="MaxInputLength"/>. The workbook is unchanged.</exception>
    public void SetCell(string address, string input)
    {
        var (sheet, cell) = ResolveAddress(address, nameof(address));
        ValidateInput(input, nameof(input));
        string current = sheet.Cells.TryGetValue(CellKey.Pack(cell), out var entry) ? entry.Input : "";
        if (string.Equals(current, input, StringComparison.Ordinal))
        {
            LastRecalc = default; // R24
            return;
        }
        Perform(new UndoStep([new CellEdit(cell, current, input) { Sheet = sheet.Id }])); // O(1) step
    }

    /// <summary>The cell's input as entered; "" for an empty cell. The address may be qualified (R83).</summary>
    /// <exception cref="ArgumentException">The address is null, invalid, a range or names an unknown sheet.</exception>
    public string GetInput(string address)
    {
        var (sheet, cell) = ResolveAddress(address, nameof(address));
        return sheet.Cells.TryGetValue(CellKey.Pack(cell), out var e) ? e.Input : "";
    }

    /// <summary>The cell's current value; <see cref="CellValue.Blank"/> for an empty cell. The address may be qualified (R83).</summary>
    /// <exception cref="ArgumentException">The address is null, invalid, a range or names an unknown sheet.</exception>
    public CellValue GetValue(string address)
    {
        var (sheet, cell) = ResolveAddress(address, nameof(address));
        return sheet.Cells.TryGetValue(CellKey.Pack(cell), out var e) ? e.Value : CellValue.Blank;
    }

    /// <summary>Test seam (§5.5, D-54): the parsed formula installed for a cell (null for a constant or empty cell), so
    /// tests can check that it is exactly what parsing the stored input gives.</summary>
    internal ParsedFormula? GetInstalledFormula(string address)
    {
        var (sheet, cell) = ResolveAddress(address, nameof(address));
        return sheet.Cells.TryGetValue(CellKey.Pack(cell), out var e) ? e.Formula : null;
    }

    /// <summary>The number of non-empty cells on every sheet, O(sheets) (the count <see cref="GetUsedAddresses"/> would
    /// return, without the sort). Internal: not part of the §9.1 surface; the App uses it to choose a selection-scan strategy.</summary>
    internal int UsedCellCount
    {
        get
        {
            int count = 0;
            foreach (var sheet in _sheets) count += sheet.Cells.Count;
            return count;
        }
    }

    /// <summary>Canonical addresses of every non-empty cell. With exactly one sheet they are unqualified and row-major
    /// (row, then column), as in v1; with several they are qualified, in sheet order then row-major (CR1.3).</summary>
    public IReadOnlyList<string> GetUsedAddresses()
    {
        var keys = new long[UsedCellCount];
        int n = 0;
        foreach (var sheet in _sheets)
            foreach (int key in sheet.Cells.Keys) keys[n++] = SheetCellKey.Pack(sheet.Id, key);
        return Array.AsReadOnly(ToSortedAddresses(keys));
    }

    /// <summary>The (unqualified canonical address, input) pairs of the non-empty cells of the sheet at
    /// <paramref name="index"/> in <see cref="SheetNames"/>, row-major (row, then column). Internal: the per-sheet cell
    /// enumeration of the version 2 writer (CR1.5, D-56); O(n log n) in the sheet's used cells.</summary>
    internal KeyValuePair<string, string>[] GetSheetCells(int index)
    {
        var cells = _sheets[index].Cells;
        // Project each key to row << 10 | column (column < 1024, row < 2^17), so a plain int sort is row-major.
        var order = new int[cells.Count];
        int n = 0;
        foreach (int key in cells.Keys) order[n++] = (CellKey.Row(key) << 10) | CellKey.Column(key);
        Array.Sort(order);
        var result = new KeyValuePair<string, string>[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            int key = CellKey.Pack(order[i] & 1023, order[i] >> 10);
            result[i] = new(CellKey.Unpack(key).ToString(), cells[key].Input);
        }
        return result;
    }

    /// <summary>An unqualified cell address (the single-sheet loader's cells and CopyPaste's internal use).</summary>
    private static CellAddress ParseAddress(string? address, string paramName)
    {
        ArgumentNullException.ThrowIfNull(address, paramName);
        if (!CellAddress.TryParse(address, out var cell))
            throw new ArgumentException($"Invalid cell address: '{address}'.", paramName);
        return cell;
    }

    /// <summary>R83: a cell address with an optional sheet prefix (R82 quoting); unqualified means the active sheet.</summary>
    /// <exception cref="ArgumentException">Null (<see cref="ArgumentNullException"/>), invalid, or an unknown sheet.</exception>
    private (Sheet Sheet, CellAddress Cell) ResolveAddress(string? address, string paramName)
    {
        ArgumentNullException.ThrowIfNull(address, paramName);
        var sheet = SplitSheet(address, paramName, out string rest);
        if (!CellAddress.TryParse(rest, out var cell))
            throw new ArgumentException($"Invalid cell address: '{Clip(address)}'.", paramName);
        return (sheet, cell);
    }

    /// <summary>The sheet named by the prefix of <paramref name="text"/> (the active sheet when unqualified) and the
    /// rest after the '!'.</summary>
    /// <exception cref="ArgumentException">A malformed prefix (R82, R83) or an unknown sheet.</exception>
    private Sheet SplitSheet(string text, string paramName, out string rest)
    {
        if (!SheetName.TrySplitQualified(text, out string? name, out rest))
            throw new ArgumentException($"Invalid address: '{Clip(text)}'.", paramName);
        if (name is null) return _active;
        return _sheetsByName.TryGetValue(name, out var sheet)
            ? sheet
            : throw new ArgumentException($"Unknown sheet '{Clip(name)}' in '{Clip(text)}'.", paramName);
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

    /// <summary>The edited cells (their inputs always changed) plus the formula cells whose value changed, formatted per R81.</summary>
    private string[] ChangedAddresses(ImmutableArray<CellEdit> edits, IReadOnlyList<long> changedFormulas)
    {
        if (edits.Length == 1 && changedFormulas.Count == 0 && _sheetsById.TryGetValue(edits[0].Sheet, out var only))
            return [FormatAddress(only, edits[0].Address)]; // P-3: O(1)
        var keys = new long[edits.Length + changedFormulas.Count];
        for (int i = 0; i < edits.Length; i++) keys[i] = SheetCellKey.Pack(edits[i].Sheet, edits[i].Address);
        for (int i = 0; i < changedFormulas.Count; i++) keys[edits.Length + i] = changedFormulas[i];
        return ToSortedAddresses(keys);
    }

    /// <summary>R81, CR1.3: an address unqualified while the workbook has exactly one sheet, else qualified.</summary>
    private string FormatAddress(Sheet sheet, CellAddress address) =>
        _sheets.Count == 1 ? address.ToString() : sheet.Prefix + address.ToString();

    /// <summary>
    /// Sorts sheet-qualified keys (<see cref="SheetCellKey"/>) in sheet order then row-major, drops duplicates and the
    /// keys of sheets that no longer exist (R81), and formats them (unqualified with one sheet, else qualified). Reuses
    /// <paramref name="keys"/> as the sort buffer: each key is projected to <c>order &lt;&lt; 32 | row &lt;&lt; 10 | column</c>
    /// (column &lt; 1024), or -1 when dropped.
    /// </summary>
    private string[] ToSortedAddresses(long[] keys)
    {
        bool single = _sheets.Count == 1;
        int onlyId = _sheets[0].Id;
        Dictionary<int, int>? order = null;
        if (!single)
        {
            order = new Dictionary<int, int>(_sheets.Count);
            for (int i = 0; i < _sheets.Count; i++) order.Add(_sheets[i].Id, i);
        }
        for (int i = 0; i < keys.Length; i++)
        {
            int sheetId = SheetCellKey.Sheet(keys[i]);
            int position;
            if (single) position = sheetId == onlyId ? 0 : -1;
            else position = order!.TryGetValue(sheetId, out int p) ? p : -1;
            int cell = SheetCellKey.Cell(keys[i]);
            keys[i] = position < 0 ? -1 : ((long)position << 32) | (uint)((CellKey.Row(cell) << 10) | CellKey.Column(cell));
        }
        Array.Sort(keys);
        int unique = 0;
        for (int i = 0; i < keys.Length; i++)
            if (keys[i] >= 0 && (i == 0 || keys[i] != keys[i - 1])) unique++;
        var result = new string[unique];
        int n = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i] < 0 || (i > 0 && keys[i] == keys[i - 1])) continue;
            int low = (int)(keys[i] & 0xFFFF_FFFFL);
            var address = new CellAddress(low & 1023, low >> 10);
            result[n++] = single ? address.ToString() : _sheets[(int)(keys[i] >> 32)].Prefix + address.ToString();
        }
        return result;
    }

    /// <summary>
    /// The store of one sheet that the dependency graph recalculates against (§6.1), including name resolution for
    /// evaluation (§9.6) and the other sheets for qualified references (CR1.2); kept private so the public surface
    /// stays §9.1.
    /// </summary>
    private sealed class SheetTarget(Workbook owner, Sheet sheet) : IRecalcTarget
    {
        public bool TryResolveName(string name, out NameTarget target)
        {
            if (!owner._names.TryGetValue(name, out var e)) { target = default; return false; }
            // R77: the target lies on the sheet its qualified refersTo names, bound by stable id (rename-safe).
            target = owner._sheetsById.TryGetValue(e.Definition.Sheet, out var on)
                ? e.Target with { Sheet = on.Name }
                : NameTarget.Invalid;
            return true;
        }

        public CellValue GetValue(CellAddress address) =>
            sheet.Cells.TryGetValue(CellKey.Pack(address), out var e) ? e.Value : CellValue.Blank;

        ICellValueProvider? ICellValueProvider.ResolveSheet(string name) =>
            owner._sheetsByName.TryGetValue(name, out var other) ? other.Target : null;

        public bool TryGetFormula(CellAddress address, [NotNullWhen(true)] out ParsedFormula? formula)
        {
            formula = sheet.Cells.TryGetValue(CellKey.Pack(address), out var e) ? e.Formula : null;
            return formula is not null;
        }

        public void SetComputedValue(CellAddress address, CellValue value)
        {
            var entry = sheet.Cells[CellKey.Pack(address)];
            if (owner._logWrites) owner._rollback.Add((entry, entry.Value));
            entry.Value = value;
        }

        IRecalcTarget? IRecalcTarget.ForSheet(int id) => owner._sheetsById.TryGetValue(id, out var s) ? s.Target : null;
    }

    /// <summary>The root target handed to the dependency graph: it only maps sheet ids to their <see cref="SheetTarget"/>;
    /// the graph reads and writes every cell through the target of the cell's sheet (D-52).</summary>
    private sealed class RecalcRoot(Workbook owner) : IRecalcTarget
    {
        public CellValue GetValue(CellAddress address) => throw NotASheet();
        public bool TryGetFormula(CellAddress address, [NotNullWhen(true)] out ParsedFormula? formula) => throw NotASheet();
        public void SetComputedValue(CellAddress address, CellValue value) => throw NotASheet();
        IRecalcTarget? IRecalcTarget.ForSheet(int id) => owner._sheetsById.TryGetValue(id, out var s) ? s.Target : null;
        private static InvalidOperationException NotASheet() => new("The recalc root is not a sheet; use ForSheet.");
    }
}

/// <summary>One sheet of a loaded file (CR1.5, D-52): its name and its (unqualified address, input) pairs.</summary>
internal readonly record struct LoadedSheet(string Name, IEnumerable<KeyValuePair<string, string>> Cells);

/// <summary>The form a loaded file must write each refersTo in, other than the exact <c>#REF!</c> (R59): either form
/// (<see cref="Any"/>, the internal loader entries), unqualified only (a version 1 file, migrated to the first sheet), or
/// sheet-qualified only (a version 2 file, R86). CR1.5, D-56.</summary>
internal enum LoadedNameForm
{
    Any,
    Unqualified,
    Qualified,
}
