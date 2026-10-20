using Gridline.Core.Addressing;
using Gridline.Core.Evaluation;
using Gridline.Core.Parsing;

namespace Gridline.Core.Dependencies;

/// <summary>
/// The static dependency graph and incremental recalculation engine (§6.1, §6.7). Forward edges map each formula cell
/// to its <see cref="FormulaPrecedents"/>; reverse lookup maps a cell to the formulas that reference it directly, and a
/// <see cref="RangeIndex"/> finds formulas whose ranges contain any cell (set, cleared or never used). Name
/// precedents are recorded by upper-cased name (R20). Traversal, cycle detection (iterative Tarjan SCC, R22) and
/// scheduling are all iterative, so call-stack depth never depends on chain length (§6.7, R9).
/// SUMIF/AVERAGEIF resized rectangles that involve a name are kept in a third RangeIndex and follow the name's target (R68, D-45).
/// <para>
/// Multiple sheets (CR1.1, CR1.2, D-52): one graph spans every sheet of a workbook. Cells are keyed by a stable internal
/// sheet id plus the cell (<see cref="SheetCellKey"/>), and each sheet has its own range indexes. An unqualified
/// precedent lies on the formula's own sheet; a qualified one is resolved through the sheet-name bindings
/// (<see cref="SetSheet"/>). A formula with a qualified precedent is also recorded under that upper-cased sheet name,
/// whether or not the sheet exists, so binding or unbinding the name re-resolves its edges and recalculates it. The
/// <see cref="CellAddress"/> members act on sheet 0, which is the only sheet of a single-sheet workbook.
/// </para>
/// Single-threaded, like the Workbook that owns it.
/// </summary>
public sealed class DependencyGraph
{
    /// <summary>The range indexes of one sheet; created on first use.</summary>
    private sealed class SheetIndexes
    {
        public readonly RangeIndex Ranges = new();  // values: formula keys
        public readonly RangeIndex Resized = new(); // values: formula keys (R68)
        public readonly RangeIndex Names = new();   // values: name ids
    }

    private readonly Dictionary<long, FormulaPrecedents> _formulas = [];
    private readonly Dictionary<long, HashSet<long>> _cellDependents = [];
    private readonly Dictionary<string, HashSet<long>> _nameDependents = new(StringComparer.Ordinal);
    private readonly Dictionary<int, SheetIndexes> _sheets = [];
    private readonly List<long> _scratch = [];

    // Sheet-name bindings (CR1.2, R82): upper-cased name -> stable sheet id, and the formulas whose text names each sheet.
    private readonly Dictionary<string, int> _sheetIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<long>> _sheetNameDependents = new(StringComparer.Ordinal);

    // Defined-name targets (§9.6, §6.7): each upper-cased name gets a stable id; a live target is registered in the name
    // range index of its sheet (values are name ids), so an edit inside the target reaches the name's dependents and
    // cycles through names are ordinary graph cycles.
    private readonly Dictionary<string, int> _nameIds = new(StringComparer.Ordinal);
    private readonly List<string> _nameById = [];
    private readonly Dictionary<int, (int Sheet, CellRange Range)> _nameTargets = [];
    private readonly List<long> _nameScratch = [];

    // Resized rectangles of SUMIF/AVERAGEIF calls that involve a name (R68, D-45): the rectangle is a function of the
    // names' current targets, so it is kept per formula (distinct rectangles), indexed in its sheet's Resized index
    // (values are formula keys), and recomputed when a name it uses gets a new target.
    private readonly Dictionary<long, (int Sheet, CellRange Range)[]> _resizedRects = [];
    private readonly Dictionary<string, HashSet<long>> _resizedNames = new(StringComparer.Ordinal);

    /// <summary>Number of formula cells (failed parses included), on every sheet.</summary>
    public int FormulaCount => _formulas.Count;

    public bool IsFormulaCell(CellAddress address) => _formulas.ContainsKey(SheetCellKey.Pack(0, address));

    /// <summary>
    /// Replaces the precedent edges of the cell at <paramref name="address"/> (§6.1). A non-null formula (even a
    /// failed parse, which has no precedents) makes it a formula cell; null means it is now a constant or blank.
    /// Call this for every edit before <see cref="Recalculate(IReadOnlyCollection{CellAddress}, IRecalcTarget, IFunctionResolver?)"/>.
    /// </summary>
    public void SetFormula(CellAddress address, ParsedFormula? formula) => SetFormula(0, address, formula);

    /// <summary>As <see cref="SetFormula(CellAddress, ParsedFormula?)"/>, for a cell of the sheet with id <paramref name="sheet"/> (D-52).</summary>
    internal void SetFormula(int sheet, CellAddress address, ParsedFormula? formula)
    {
        var precedents = formula is null ? null : PrecedentExtractor.Extract(formula); // before any mutation
        long key = SheetCellKey.Pack(sheet, address);
        if (_formulas.Remove(key, out var old)) RemoveEdges(key, old);
        if (precedents is null) return;
        _formulas.Add(key, precedents);
        AddEdges(key, precedents);
    }

    /// <summary>
    /// Binds the sheet name <paramref name="name"/> (any casing, R82) to the sheet id <paramref name="sheet"/>, or
    /// unbinds it when null (CR1.1, D-52). Every formula whose text names that sheet gets its edges re-resolved; call
    /// <see cref="Recalculate(IReadOnlyList{long}, IReadOnlyCollection{string}?, IReadOnlyCollection{string}?, IRecalcTarget, IFunctionResolver?)"/>
    /// with the name among the changed sheets to re-evaluate them. Idempotent.
    /// </summary>
    internal void SetSheet(string name, int? sheet)
    {
        ArgumentNullException.ThrowIfNull(name);
        string upper = name.ToUpperInvariant();
        long[] users = _sheetNameDependents.TryGetValue(upper, out var set) ? [.. set] : [];
        foreach (long key in users) RemoveEdges(key, _formulas[key]);
        if (sheet is { } id) _sheetIds[upper] = id;
        else _sheetIds.Remove(upper);
        foreach (long key in users) AddEdges(key, _formulas[key]);
    }

    /// <summary>The recorded precedents of a formula cell; <see cref="FormulaPrecedents.Empty"/> for any other cell.</summary>
    public FormulaPrecedents GetPrecedents(CellAddress address) =>
        _formulas.TryGetValue(SheetCellKey.Pack(0, address), out var p) ? p : FormulaPrecedents.Empty;

    /// <summary>The distinct formula cells that reference <paramref name="address"/> directly or through a range.</summary>
    public IReadOnlyCollection<CellAddress> GetDependents(CellAddress address)
    {
        var keys = new List<long>();
        CollectDependents(SheetCellKey.Pack(0, address), keys);
        var result = new HashSet<CellAddress>();
        foreach (long k in keys) result.Add(SheetCellKey.Address(k));
        return result;
    }

    /// <summary>The formula cells whose text references the name (case-insensitive, R20). Names are definable from Phase 4.</summary>
    public IReadOnlyCollection<CellAddress> GetNameDependents(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_nameDependents.TryGetValue(name.ToUpperInvariant(), out var set)) return [];
        var result = new List<CellAddress>(set.Count);
        foreach (long k in set) result.Add(SheetCellKey.Address(k));
        return result;
    }

    /// <summary>
    /// Recalculates after an edit (§6.1, §6.7). The affected set is every formula cell in <paramref name="changed"/>
    /// plus all their transitive dependents (iterative BFS). Members of cyclic SCCs (size > 1 or a self-edge) get
    /// #CIRC!; every other affected formula is evaluated exactly once, in topological order, against
    /// <paramref name="target"/> (R22), so downstream cells see #CIRC! as an ordinary error.
    /// Edges must already reflect the edit (<see cref="SetFormula(CellAddress, ParsedFormula?)"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">An affected formula cell has no formula in the target; thrown before any value is written.</exception>
    public RecalcResult Recalculate(IReadOnlyCollection<CellAddress> changed, IRecalcTarget target, IFunctionResolver? functions)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(target);
        return Recalculate(ToKeys(changed), null, null, target, functions);
    }

    /// <summary>
    /// As <see cref="Recalculate(IReadOnlyCollection{CellAddress}, IRecalcTarget, IFunctionResolver?)"/>, with
    /// <paramref name="changedNames"/> also seeding the affected set: each formula that references a changed name
    /// (defined, redefined or deleted, §9.6) and all its transitive dependents. Only those re-evaluate (§6.7). Name
    /// targets must already be updated (<see cref="SetNameTarget(string, CellRange?)"/>).
    /// </summary>
    public RecalcResult Recalculate(IReadOnlyCollection<CellAddress> changed, IReadOnlyCollection<string> changedNames,
        IRecalcTarget target, IFunctionResolver? functions)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(changedNames);
        ArgumentNullException.ThrowIfNull(target);
        return Recalculate(ToKeys(changed), changedNames, null, target, functions);
    }

    /// <summary>
    /// The sheet-aware recalculation (D-52): <paramref name="changed"/> are <see cref="SheetCellKey"/> keys;
    /// <paramref name="changedNames"/> seeds the formulas that reference those names and <paramref name="changedSheets"/>
    /// the formulas whose text names those sheets (a sheet was created or removed, R82). Otherwise as above.
    /// </summary>
    internal RecalcResult Recalculate(IReadOnlyList<long> changed, IReadOnlyCollection<string>? changedNames,
        IReadOnlyCollection<string>? changedSheets, IRecalcTarget target, IFunctionResolver? functions)
    {
        Schedule? schedule = changedNames is { Count: > 0 } || changedSheets is { Count: > 0 } ? new Schedule(16) : null;
        if (changedNames is not null)
            foreach (var name in changedNames)
            {
                _scratch.Clear();
                CollectNameDependents(name.ToUpperInvariant(), _scratch);
                foreach (long d in _scratch) (schedule ??= new Schedule(16)).AddNode(d);
            }
        if (changedSheets is not null)
            foreach (var sheet in changedSheets)
                if (_sheetNameDependents.TryGetValue(sheet.ToUpperInvariant(), out var users))
                    foreach (long d in users) (schedule ??= new Schedule(16)).AddNode(d);
        if (schedule is not null)
        {
            // The order of the Phase 4 name path: each changed cell in turn, a formula as a node, else its dependents.
            for (int i = 0; i < changed.Count; i++)
            {
                long key = changed[i];
                if (_formulas.ContainsKey(key)) { schedule.AddNode(key); continue; }
                _scratch.Clear();
                CollectDependents(key, _scratch);
                foreach (long d in _scratch) schedule.AddNode(d);
            }
            return schedule.Nodes.Count == 0 ? RecalcResult.None : Run(schedule, target, functions);
        }
        for (int i = 0; i < changed.Count; i++)
        {
            long key = changed[i];
            if (_formulas.ContainsKey(key)) (schedule ??= new Schedule(16)).AddNode(key);
        }
        for (int i = 0; i < changed.Count; i++)
        {
            long key = changed[i];
            if (_formulas.ContainsKey(key)) continue; // already a node; its dependents come from the BFS
            _scratch.Clear();
            CollectDependents(key, _scratch);
            if (_scratch.Count == 0) continue;
            schedule ??= new Schedule(16);
            foreach (long d in _scratch) schedule.AddNode(d);
        }
        if (schedule is null) return RecalcResult.None; // P-3: an edit with no dependents
        return Run(schedule, target, functions);
    }

    /// <summary>
    /// Records the target of a defined name (§9.6, §6.7), case-insensitively (R20): every formula referencing the name
    /// then depends on each cell of <paramref name="target"/>. Null means the name is undefined or its target was
    /// deleted (#REF!): it has no target cells. Idempotent. The formulas that reference the name are already known from
    /// their text (<see cref="SetFormula(CellAddress, ParsedFormula?)"/>), defined or not, so defining a name later resolves them.
    /// </summary>
    public void SetNameTarget(string name, CellRange? target) => SetNameTarget(name, 0, target);

    /// <summary>As <see cref="SetNameTarget(string, CellRange?)"/>, with the target on the sheet with id <paramref name="sheet"/> (D-52).</summary>
    internal void SetNameTarget(string name, int sheet, CellRange? target)
    {
        ArgumentNullException.ThrowIfNull(name);
        string upper = name.ToUpperInvariant();
        if (!_nameIds.TryGetValue(upper, out int id))
        {
            if (target is null) return;
            id = _nameById.Count;
            _nameIds.Add(upper, id);
            _nameById.Add(upper);
        }
        if (_nameTargets.Remove(id, out var old)) _sheets[old.Sheet].Names.Remove(old.Range, id);
        if (target is { } range)
        {
            _nameTargets.Add(id, (sheet, range));
            Indexes(sheet).Names.Add(range, id);
        }
        if (_resizedNames.TryGetValue(upper, out var users))
            foreach (long key in users) RefreshResized(key, _formulas[key]); // R68: the resized rectangle follows the name
    }

    /// <summary>Recalculates every formula cell (e.g. after a load); same cycle and ordering rules as <see cref="Recalculate(IReadOnlyCollection{CellAddress}, IRecalcTarget, IFunctionResolver?)"/>.</summary>
    /// <exception cref="InvalidOperationException">A formula cell has no formula in the target; thrown before any value is written.</exception>
    public RecalcResult RecalculateAll(IRecalcTarget target, IFunctionResolver? functions)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_formulas.Count == 0) return RecalcResult.None;
        var schedule = new Schedule(_formulas.Count);
        foreach (long key in _formulas.Keys) schedule.AddNode(key);
        return Run(schedule, target, functions);
    }

    private static long[] ToKeys(IReadOnlyCollection<CellAddress> changed)
    {
        var keys = new long[changed.Count];
        int i = 0;
        foreach (var address in changed) keys[i++] = SheetCellKey.Pack(0, address);
        return keys;
    }

    private SheetIndexes Indexes(int sheet)
    {
        if (!_sheets.TryGetValue(sheet, out var indexes)) _sheets.Add(sheet, indexes = new SheetIndexes());
        return indexes;
    }

    /// <summary>The sheet id a precedent lies on: the formula's own sheet when unqualified, else the bound sheet;
    /// false when the named sheet does not exist (the reference is #REF!, R82, and has no cell edges).</summary>
    private bool TryResolveSheet(string? upperSheet, int ownSheet, out int sheet)
    {
        if (upperSheet is null) { sheet = ownSheet; return true; }
        return _sheetIds.TryGetValue(upperSheet, out sheet);
    }

    /// <summary>Adds every edge of a formula, resolving qualified precedents through the current sheet bindings.
    /// <see cref="RemoveEdges"/> undoes exactly this as long as the bindings did not change in between, which
    /// <see cref="SetSheet"/> guarantees by re-edging the affected formulas around a binding change.</summary>
    private void AddEdges(long key, FormulaPrecedents p)
    {
        int own = SheetCellKey.Sheet(key);
        foreach (var cell in p.CellPrecedents)
        {
            if (cell.Sheet is not null) TrackSheetName(cell.Sheet, key);
            if (!TryResolveSheet(cell.Sheet, own, out int sheet)) continue;
            long ck = SheetCellKey.Pack(sheet, cell.Address);
            if (!_cellDependents.TryGetValue(ck, out var set)) _cellDependents.Add(ck, set = []);
            set.Add(key);
        }
        foreach (var range in p.RangePrecedents)
        {
            if (range.Sheet is not null) TrackSheetName(range.Sheet, key);
            if (TryResolveSheet(range.Sheet, own, out int sheet)) Indexes(sheet).Ranges.Add(range.Range, key);
        }
        foreach (var name in p.Names)
        {
            if (!_nameDependents.TryGetValue(name, out var set)) _nameDependents.Add(name, set = []);
            set.Add(key);
        }
        if (p.Resized.Count > 0)
        {
            foreach (var rr in p.Resized)
            {
                TrackResizedName(rr.Range, key);
                TrackResizedName(rr.Anchor, key);
            }
            RefreshResized(key, p);
        }
    }

    private void RemoveEdges(long key, FormulaPrecedents old)
    {
        int own = SheetCellKey.Sheet(key);
        foreach (var cell in old.CellPrecedents)
        {
            if (cell.Sheet is not null) UntrackSheetName(cell.Sheet, key);
            if (!TryResolveSheet(cell.Sheet, own, out int sheet)) continue;
            long ck = SheetCellKey.Pack(sheet, cell.Address);
            if (_cellDependents.TryGetValue(ck, out var set) && set.Remove(key) && set.Count == 0) _cellDependents.Remove(ck);
        }
        foreach (var range in old.RangePrecedents)
        {
            if (range.Sheet is not null) UntrackSheetName(range.Sheet, key);
            if (TryResolveSheet(range.Sheet, own, out int sheet) && _sheets.TryGetValue(sheet, out var indexes))
                indexes.Ranges.Remove(range.Range, key);
        }
        foreach (var name in old.Names)
        {
            if (_nameDependents.TryGetValue(name, out var set) && set.Remove(key) && set.Count == 0) _nameDependents.Remove(name);
        }
        if (old.Resized.Count == 0) return;
        RemoveResizedRects(key);
        foreach (var rr in old.Resized)
        {
            UntrackResizedName(rr.Range, key);
            UntrackResizedName(rr.Anchor, key);
        }
    }

    private void TrackSheetName(string upperSheet, long key)
    {
        if (!_sheetNameDependents.TryGetValue(upperSheet, out var set)) _sheetNameDependents.Add(upperSheet, set = []);
        set.Add(key);
    }

    private void UntrackSheetName(string upperSheet, long key)
    {
        if (_sheetNameDependents.TryGetValue(upperSheet, out var set) && set.Remove(key) && set.Count == 0)
            _sheetNameDependents.Remove(upperSheet);
    }

    private void TrackResizedName(ReferenceSpec spec, long key)
    {
        if (spec.Name is not { } n) return;
        if (!_resizedNames.TryGetValue(n, out var set)) _resizedNames.Add(n, set = []);
        set.Add(key);
    }

    private void UntrackResizedName(ReferenceSpec spec, long key)
    {
        if (spec.Name is { } n && _resizedNames.TryGetValue(n, out var set) && set.Remove(key) && set.Count == 0)
            _resizedNames.Remove(n);
    }

    private void RemoveResizedRects(long key)
    {
        if (_resizedRects.Remove(key, out var old))
            foreach (var (sheet, r) in old) _sheets[sheet].Resized.Remove(r, key);
    }

    /// <summary>Recomputes the resized rectangles of one formula from the current name targets and sheet bindings
    /// (R68). The rectangle lies on the sheet of the anchor (<c>sum_range</c>, CR1.2).</summary>
    private void RefreshResized(long key, FormulaPrecedents p)
    {
        RemoveResizedRects(key);
        int own = SheetCellKey.Sheet(key);
        var rects = new List<(int Sheet, CellRange Range)>(p.Resized.Count);
        foreach (var rr in p.Resized)
        {
            // An undefined or deleted name, or a missing sheet, has no cells.
            if (!TryResolve(rr.Range, own, out _, out var range) || !TryResolve(rr.Anchor, own, out int sheet, out var anchor)) continue;
            var rect = (sheet, ResizedReference.Resize(range, anchor));
            if (!rects.Contains(rect)) rects.Add(rect);
        }
        if (rects.Count == 0) return;
        var array = rects.ToArray();
        _resizedRects.Add(key, array);
        foreach (var (sheet, r) in array) Indexes(sheet).Resized.Add(r, key);
    }

    private bool TryResolve(ReferenceSpec spec, int ownSheet, out int sheet, out CellRange range)
    {
        if (spec.Literal is { } literal && TryResolveSheet(spec.Sheet, ownSheet, out sheet))
        {
            range = literal;
            return true;
        }
        if (spec.Name is { } name && _nameIds.TryGetValue(name, out int id) && _nameTargets.TryGetValue(id, out var t))
        {
            (sheet, range) = t;
            return true;
        }
        sheet = 0;
        range = default;
        return false;
    }

    /// <summary>Appends the formulas that reference the cell directly or through a range (duplicates possible).</summary>
    private void CollectDependents(long key, List<long> output)
    {
        if (_cellDependents.TryGetValue(key, out var direct))
            foreach (long d in direct) output.Add(d);
        if (!_sheets.TryGetValue(SheetCellKey.Sheet(key), out var indexes)) return;
        int cell = SheetCellKey.Cell(key);
        int column = CellKey.Column(cell), row = CellKey.Row(cell);
        indexes.Ranges.CollectDependents(column, row, output);
        if (indexes.Resized.RangeCount > 0) indexes.Resized.CollectDependents(column, row, output);
        if (indexes.Names.RangeCount == 0) return; // P-3: no defined names on this sheet, no extra work
        _nameScratch.Clear();
        indexes.Names.CollectDependents(column, row, _nameScratch);
        foreach (long id in _nameScratch) CollectNameDependents(_nameById[(int)id], output);
    }

    /// <summary>Appends the formulas whose text references the upper-cased name.</summary>
    private void CollectNameDependents(string upperName, List<long> output)
    {
        if (_nameDependents.TryGetValue(upperName, out var set))
            foreach (long d in set) output.Add(d);
    }

    /// <summary>The affected subgraph: nodes in discovery order and their dependent edges in CSR form.</summary>
    private sealed class Schedule(int capacity)
    {
        public readonly List<long> Nodes = new(capacity);
        public readonly Dictionary<long, int> Index = new(capacity);
        public int[] EdgeStart = [];
        public int[] Edges = [];

        public int AddNode(long key)
        {
            if (Index.TryGetValue(key, out int i)) return i;
            i = Nodes.Count;
            Index.Add(key, i);
            Nodes.Add(key);
            return i;
        }
    }

    private RecalcResult Run(Schedule s, IRecalcTarget target, IFunctionResolver? functions)
    {
        // 1. Iterative BFS: discover transitive dependents and record edges node -> dependent (CSR).
        var edgeStart = new List<int>(s.Nodes.Count + 1);
        var edges = new List<int>(s.Nodes.Count);
        for (int n = 0; n < s.Nodes.Count; n++)
        {
            edgeStart.Add(edges.Count);
            _scratch.Clear();
            CollectDependents(s.Nodes[n], _scratch);
            foreach (long d in _scratch) edges.Add(s.AddNode(d));
        }
        edgeStart.Add(edges.Count);
        int count = s.Nodes.Count;
        s.EdgeStart = [.. edgeStart];
        s.Edges = [.. edges];

        // Fetch every formula (through the target of its sheet) before writing anything, so an inconsistent target fails cleanly.
        var addresses = new CellAddress[count];
        var formulas = new ParsedFormula[count];
        var targets = new IRecalcTarget[count];
        int lastSheet = -1;
        IRecalcTarget? sheetTarget = null;
        for (int n = 0; n < count; n++)
        {
            long key = s.Nodes[n];
            int sheet = SheetCellKey.Sheet(key);
            if (sheet != lastSheet)
            {
                sheetTarget = target.ForSheet(sheet);
                lastSheet = sheet;
            }
            addresses[n] = SheetCellKey.Address(key);
            if (sheetTarget is null || !sheetTarget.TryGetFormula(addresses[n], out var f))
                throw new InvalidOperationException($"Cell {addresses[n]} is a formula cell in the dependency graph but has no formula in the target.");
            formulas[n] = f;
            targets[n] = sheetTarget;
        }

        // 2. Iterative Tarjan: SCCs are emitted sinks first, i.e. in reverse topological order.
        var (order, sccEnd) = StronglyConnectedComponents(count, s.EdgeStart, s.Edges);

        // 3. Evaluate in topological order (last emitted SCC first); cyclic SCCs become #CIRC! (R22).
        var circ = CellValue.FromError(ErrorCodes.Circ);
        var changedKeys = new List<long>();
        for (int c = sccEnd.Count - 1; c >= 0; c--)
        {
            int start = c == 0 ? 0 : sccEnd[c - 1], end = sccEnd[c];
            bool cyclic = end - start > 1 || HasSelfEdge(order[start], s.EdgeStart, s.Edges);
            for (int k = start; k < end; k++)
            {
                int n = order[k];
                var t = targets[n];
                var address = addresses[n];
                var value = cyclic ? circ : FormulaEvaluator.Evaluate(formulas[n], t, functions, t, address);
                if (t.GetValue(address) != value) changedKeys.Add(s.Nodes[n]);
                t.SetComputedValue(address, value);
            }
        }
        return new RecalcResult(count, changedKeys); // R21: every affected formula, #CIRC! included
    }

    private static bool HasSelfEdge(int node, int[] edgeStart, int[] edges)
    {
        for (int e = edgeStart[node]; e < edgeStart[node + 1]; e++)
            if (edges[e] == node) return true;
        return false;
    }

    /// <summary>
    /// Iterative Tarjan over nodes 0..count-1 (explicit call stack, §6.7). Returns the nodes grouped by SCC in emission
    /// order (reverse topological for edges precedent -> dependent) and the exclusive end offset of each SCC.
    /// </summary>
    private static (int[] Order, List<int> SccEnd) StronglyConnectedComponents(int count, int[] edgeStart, int[] edges)
    {
        var index = new int[count]; // 0 = unvisited; otherwise discovery number (1-based)
        var low = new int[count];
        var onStack = new bool[count];
        var stack = new int[count];
        int sp = 0;
        var callNode = new int[count];
        var callEdge = new int[count];
        int csp = 0;
        var order = new int[count];
        int written = 0;
        var sccEnd = new List<int>();
        int counter = 0;

        for (int root = 0; root < count; root++)
        {
            if (index[root] != 0) continue;
            index[root] = low[root] = ++counter;
            stack[sp++] = root; onStack[root] = true;
            callNode[csp] = root; callEdge[csp] = edgeStart[root]; csp++;

            while (csp > 0)
            {
                int v = callNode[csp - 1];
                int e = callEdge[csp - 1];
                if (e < edgeStart[v + 1])
                {
                    callEdge[csp - 1] = e + 1;
                    int w = edges[e];
                    if (index[w] == 0)
                    {
                        index[w] = low[w] = ++counter;
                        stack[sp++] = w; onStack[w] = true;
                        callNode[csp] = w; callEdge[csp] = edgeStart[w]; csp++;
                    }
                    else if (onStack[w] && index[w] < low[v])
                    {
                        low[v] = index[w];
                    }
                    continue;
                }

                csp--;
                if (low[v] == index[v])
                {
                    int w;
                    do
                    {
                        w = stack[--sp];
                        onStack[w] = false;
                        order[written++] = w;
                    } while (w != v);
                    sccEnd.Add(written);
                }
                if (csp > 0)
                {
                    int u = callNode[csp - 1];
                    if (low[v] < low[u]) low[u] = low[v];
                }
            }
        }
        return (order, sccEnd);
    }
}
