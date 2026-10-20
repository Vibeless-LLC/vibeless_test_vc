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
/// Single-threaded, like the Workbook that owns it.
/// </summary>
public sealed class DependencyGraph
{
    private readonly Dictionary<int, FormulaPrecedents> _formulas = [];
    private readonly Dictionary<int, HashSet<int>> _cellDependents = [];
    private readonly Dictionary<string, HashSet<int>> _nameDependents = new(StringComparer.Ordinal);
    private readonly RangeIndex _ranges = new();
    private readonly List<int> _scratch = [];

    // Defined-name targets (§9.6, §6.7): each upper-cased name gets a stable id; a live target is registered in its own
    // range index (values are name ids), so an edit inside the target reaches the name's dependents and cycles through
    // names are ordinary graph cycles.
    private readonly Dictionary<string, int> _nameIds = new(StringComparer.Ordinal);
    private readonly List<string> _nameById = [];
    private readonly Dictionary<int, CellRange> _nameTargets = [];
    private readonly RangeIndex _nameRanges = new();
    private readonly List<int> _nameScratch = [];

    /// <summary>Number of formula cells (failed parses included).</summary>
    public int FormulaCount => _formulas.Count;

    public bool IsFormulaCell(CellAddress address) => _formulas.ContainsKey(CellKey.Pack(address));

    /// <summary>
    /// Replaces the precedent edges of the cell at <paramref name="address"/> (§6.1). A non-null formula (even a
    /// failed parse, which has no precedents) makes it a formula cell; null means it is now a constant or blank.
    /// Call this for every edit before <see cref="Recalculate"/>.
    /// </summary>
    public void SetFormula(CellAddress address, ParsedFormula? formula)
    {
        var precedents = formula is null ? null : PrecedentExtractor.Extract(formula); // before any mutation
        int key = CellKey.Pack(address);
        if (_formulas.Remove(key, out var old)) RemoveEdges(key, old);
        if (precedents is null) return;
        _formulas.Add(key, precedents);
        foreach (var cell in precedents.Cells)
        {
            int ck = CellKey.Pack(cell);
            if (!_cellDependents.TryGetValue(ck, out var set)) _cellDependents.Add(ck, set = []);
            set.Add(key);
        }
        foreach (var range in precedents.Ranges) _ranges.Add(range, key);
        foreach (var name in precedents.Names)
        {
            if (!_nameDependents.TryGetValue(name, out var set)) _nameDependents.Add(name, set = []);
            set.Add(key);
        }
    }

    /// <summary>The recorded precedents of a formula cell; <see cref="FormulaPrecedents.Empty"/> for any other cell.</summary>
    public FormulaPrecedents GetPrecedents(CellAddress address) =>
        _formulas.TryGetValue(CellKey.Pack(address), out var p) ? p : FormulaPrecedents.Empty;

    /// <summary>The distinct formula cells that reference <paramref name="address"/> directly or through a range.</summary>
    public IReadOnlyCollection<CellAddress> GetDependents(CellAddress address)
    {
        var keys = new List<int>();
        CollectDependents(CellKey.Pack(address), keys);
        var result = new HashSet<CellAddress>();
        foreach (int k in keys) result.Add(CellKey.Unpack(k));
        return result;
    }

    /// <summary>The formula cells whose text references the name (case-insensitive, R20). Names are definable from Phase 4.</summary>
    public IReadOnlyCollection<CellAddress> GetNameDependents(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_nameDependents.TryGetValue(name.ToUpperInvariant(), out var set)) return [];
        var result = new List<CellAddress>(set.Count);
        foreach (int k in set) result.Add(CellKey.Unpack(k));
        return result;
    }

    /// <summary>
    /// Recalculates after an edit (§6.1, §6.7). The affected set is every formula cell in <paramref name="changed"/>
    /// plus all their transitive dependents (iterative BFS). Members of cyclic SCCs (size > 1 or a self-edge) get
    /// #CIRC!; every other affected formula is evaluated exactly once, in topological order, against
    /// <paramref name="target"/> (R22), so downstream cells see #CIRC! as an ordinary error.
    /// Edges must already reflect the edit (<see cref="SetFormula"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">An affected formula cell has no formula in the target; thrown before any value is written.</exception>
    public RecalcResult Recalculate(IReadOnlyCollection<CellAddress> changed, IRecalcTarget target, IFunctionResolver? functions)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(target);

        Schedule? schedule = null;
        foreach (var address in changed)
        {
            int key = CellKey.Pack(address);
            if (_formulas.ContainsKey(key)) (schedule ??= new Schedule(16)).AddNode(key);
        }
        foreach (var address in changed)
        {
            int key = CellKey.Pack(address);
            if (_formulas.ContainsKey(key)) continue; // already a node; its dependents come from the BFS
            _scratch.Clear();
            CollectDependents(key, _scratch);
            if (_scratch.Count == 0) continue;
            schedule ??= new Schedule(16);
            foreach (int d in _scratch) schedule.AddNode(d);
        }
        if (schedule is null) return RecalcResult.None; // P-3: an edit with no dependents
        return Run(schedule, target, functions);
    }

    /// <summary>
    /// Records the target of a defined name (§9.6, §6.7), case-insensitively (R20): every formula referencing the name
    /// then depends on each cell of <paramref name="target"/>. Null means the name is undefined or its target was
    /// deleted (#REF!): it has no target cells. Idempotent. The formulas that reference the name are already known from
    /// their text (<see cref="SetFormula"/>), defined or not, so defining a name later resolves them.
    /// </summary>
    public void SetNameTarget(string name, CellRange? target)
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
        if (_nameTargets.Remove(id, out var old)) _nameRanges.Remove(old, id);
        if (target is { } range)
        {
            _nameTargets.Add(id, range);
            _nameRanges.Add(range, id);
        }
    }

    /// <summary>The registered target of a name (case-insensitive), or null.</summary>
    public CellRange? GetNameTarget(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _nameIds.TryGetValue(name.ToUpperInvariant(), out int id) && _nameTargets.TryGetValue(id, out var r) ? r : null;
    }

    /// <summary>
    /// As <see cref="Recalculate(IReadOnlyCollection{CellAddress}, IRecalcTarget, IFunctionResolver?)"/>, with
    /// <paramref name="changedNames"/> also seeding the affected set: each formula that references a changed name
    /// (defined, redefined or deleted, §9.6) and all its transitive dependents. Only those re-evaluate (§6.7). Name
    /// targets must already be updated (<see cref="SetNameTarget"/>).
    /// </summary>
    public RecalcResult Recalculate(IReadOnlyCollection<CellAddress> changed, IReadOnlyCollection<string> changedNames,
        IRecalcTarget target, IFunctionResolver? functions)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(changedNames);
        ArgumentNullException.ThrowIfNull(target);
        if (changedNames.Count == 0) return Recalculate(changed, target, functions);

        var schedule = new Schedule(16);
        foreach (var name in changedNames)
        {
            _scratch.Clear();
            CollectNameDependents(name.ToUpperInvariant(), _scratch);
            foreach (int d in _scratch) schedule.AddNode(d);
        }
        foreach (var address in changed)
        {
            int key = CellKey.Pack(address);
            if (_formulas.ContainsKey(key)) { schedule.AddNode(key); continue; }
            _scratch.Clear();
            CollectDependents(key, _scratch);
            foreach (int d in _scratch) schedule.AddNode(d);
        }
        if (schedule.Nodes.Count == 0) return RecalcResult.None;
        return Run(schedule, target, functions);
    }

    /// <summary>Recalculates every formula cell (e.g. after a load); same cycle and ordering rules as <see cref="Recalculate"/>.</summary>
    /// <exception cref="InvalidOperationException">A formula cell has no formula in the target; thrown before any value is written.</exception>
    public RecalcResult RecalculateAll(IRecalcTarget target, IFunctionResolver? functions)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_formulas.Count == 0) return RecalcResult.None;
        var schedule = new Schedule(_formulas.Count);
        foreach (int key in _formulas.Keys) schedule.AddNode(key);
        return Run(schedule, target, functions);
    }

    private void RemoveEdges(int key, FormulaPrecedents old)
    {
        foreach (var cell in old.Cells)
        {
            int ck = CellKey.Pack(cell);
            if (_cellDependents.TryGetValue(ck, out var set) && set.Remove(key) && set.Count == 0) _cellDependents.Remove(ck);
        }
        foreach (var range in old.Ranges) _ranges.Remove(range, key);
        foreach (var name in old.Names)
        {
            if (_nameDependents.TryGetValue(name, out var set) && set.Remove(key) && set.Count == 0) _nameDependents.Remove(name);
        }
    }

    /// <summary>Appends the formulas that reference the cell directly or through a range (duplicates possible).</summary>
    private void CollectDependents(int key, List<int> output)
    {
        if (_cellDependents.TryGetValue(key, out var direct))
            foreach (int d in direct) output.Add(d);
        _ranges.CollectDependents(CellKey.Column(key), CellKey.Row(key), output);
        if (_nameRanges.RangeCount == 0) return; // P-3: no defined names, no extra work
        _nameScratch.Clear();
        _nameRanges.CollectDependents(CellKey.Column(key), CellKey.Row(key), _nameScratch);
        foreach (int id in _nameScratch) CollectNameDependents(_nameById[id], output);
    }

    /// <summary>Appends the formulas whose text references the upper-cased name.</summary>
    private void CollectNameDependents(string upperName, List<int> output)
    {
        if (_nameDependents.TryGetValue(upperName, out var set))
            foreach (int d in set) output.Add(d);
    }

    /// <summary>The affected subgraph: nodes in discovery order and their dependent edges in CSR form.</summary>
    private sealed class Schedule(int capacity)
    {
        public readonly List<int> Nodes = new(capacity);
        public readonly Dictionary<int, int> Index = new(capacity);
        public int[] EdgeStart = [];
        public int[] Edges = [];

        public int AddNode(int key)
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
            foreach (int d in _scratch) edges.Add(s.AddNode(d));
        }
        edgeStart.Add(edges.Count);
        int count = s.Nodes.Count;
        s.EdgeStart = [.. edgeStart];
        s.Edges = [.. edges];

        // Fetch every formula before writing anything, so an inconsistent target fails cleanly.
        var addresses = new CellAddress[count];
        var formulas = new ParsedFormula[count];
        for (int n = 0; n < count; n++)
        {
            addresses[n] = CellKey.Unpack(s.Nodes[n]);
            if (!target.TryGetFormula(addresses[n], out var f))
                throw new InvalidOperationException($"Cell {addresses[n]} is a formula cell in the dependency graph but has no formula in the target.");
            formulas[n] = f;
        }

        // 2. Iterative Tarjan: SCCs are emitted sinks first, i.e. in reverse topological order.
        var (order, sccEnd) = StronglyConnectedComponents(count, s.EdgeStart, s.Edges);

        // 3. Evaluate in topological order (last emitted SCC first); cyclic SCCs become #CIRC! (R22).
        var circ = CellValue.FromError(ErrorCodes.Circ);
        var changedCells = new List<CellAddress>();
        for (int c = sccEnd.Count - 1; c >= 0; c--)
        {
            int start = c == 0 ? 0 : sccEnd[c - 1], end = sccEnd[c];
            bool cyclic = end - start > 1 || HasSelfEdge(order[start], s.EdgeStart, s.Edges);
            for (int k = start; k < end; k++)
            {
                int n = order[k];
                var value = cyclic ? circ : FormulaEvaluator.Evaluate(formulas[n], target, functions, target);
                var address = addresses[n];
                if (target.GetValue(address) != value) changedCells.Add(address);
                target.SetComputedValue(address, value);
            }
        }
        return new RecalcResult(count, changedCells); // R21: every affected formula, #CIRC! included
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
