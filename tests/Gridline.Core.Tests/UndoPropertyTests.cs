using FsCheck;
using FsCheck.Fluent;
using Gridline.Core.Addressing;
using Gridline.Core.Commands;
using Gridline.Core.Functions;
using Gridline.Core.Parsing;

namespace Gridline.Core.Tests;

// Requirement IDs: §14 (undo/redo property: a random operation sequence followed by undo-all returns the initial
// inputs and names), §9.3 (one step per mutating call, no-op calls, redo clearing, UndoLimit drop, dirty tracking), §9.4
// (CopyPaste: snapshot, overlap, blank clears, shifting, grid-edge rejection, one step), §9.5 (insert/delete rows and
// columns, R61-R64), §9.6 (DefineName, DeleteName, R58-R60), §6.7.
public class UndoPropertyTests
{
    private static readonly string[] Pool = ["A1", "B1", "C1", "A2", "B2", "C2"];
    private static readonly string[] Corner = ["ZY99999", "ZZ99999", "ZY100000", "ZZ100000"]; // bottom-right grid corner

    private abstract record Op;
    private sealed record SetOp(string Address, string Input) : Op;
    private sealed record BatchOp(KeyValuePair<string, string>[] Edits) : Op;
    private sealed record BurstOp(int Count) : Op; // many distinct SetCell steps, to reach the UndoLimit drop
    private sealed record PasteOp(string Source, string Destination) : Op;
    private sealed record StructureOp(string Kind, int Start, int Count) : Op; // IR, DR, IC, DC (§9.5)
    private sealed record DefineOp(string Name, string RefersTo) : Op;
    private sealed record DeleteNameOp(string Name) : Op;
    private sealed record UndoOp : Op;
    private sealed record RedoOp : Op;
    private sealed record MarkSavedOp : Op;

    private static readonly Gen<string> InputGen = Gen.OneOf(
        Gen.Choose(-50, 50).Select(n => n.ToString()),
        Gen.Elements("1.0", "0", "-0", "1e3", "abc", "", "", "TRUE", "false", "'=quoted"),
        Gen.Elements(
            "=A1+B1", "=SUM(A1:C2)", "=A2*2", "=B2&\"x\"", "=IF(C1>0,A1,B1)", "=C2", "=1/0", "=  a1 + 1",
            "=A1", "=B1", "=C1", "=A2",          // self- and mutual cycles depending on where they land
            "=SUM(A1:B1)", "=1+", "=SUM(", "=)", "=FOO(1)",
            "=$A$1+B$2", "=SUM($A1:C$2)", "=zz100000", "=SUM(ZY99999:$ZZ$100000)", // absolute, mixed, grid edge
            "= rate * 2", "=SUM(Total)", "=x_1+A1", "=SUM(C2:A1)", "=B2+$C$1", // names, a bottom-up range
            // R68 resized rectangles with names as range or sum_range (refreshed on SetNameTarget), and the current cell
            "=SUMIF(A1:A2,\">0\",Total)", "=SUMIF(Total,\">0\",B1)", "=AVERAGEIF(A1:B1,\"<>\",C1)",
            "=AVERAGEIF(rate,\"<>\",x_1)", "=ROW()+COLUMN()", "=ROW(B2)*COLUMN(Total)"));

    /// <summary>The address as written canonically, in lower case, or fully absolute.</summary>
    private static Gen<string> Spellings(string a)
    {
        int letters = a.TakeWhile(char.IsLetter).Count();
        return Gen.Elements(a, a.ToLowerInvariant(), "$" + a[..letters] + "$" + a[letters..]);
    }

    private static readonly Gen<string> AddressGen = Gen.Elements(Pool).SelectMany(Spellings);

    private static readonly Gen<KeyValuePair<string, string>> EditGen =
        from a in AddressGen
        from i in InputGen
        select new KeyValuePair<string, string>(a, i);

    // A source inside one block (corners in any order, or a single cell) and a destination that overlaps it, lands in
    // the other block, sits next to it, or runs off a grid edge (which must throw and change nothing).
    private static readonly Gen<Op> PasteGen =
        from block in Gen.Elements(Pool, Corner)
        from a in Gen.Elements(block)
        from b in Gen.Elements(block)
        from single in Gen.Elements(false, false, true)
        from sa in Spellings(a)
        from sb in Spellings(b)
        from dest in Gen.Elements([.. Pool, .. Corner, "D1", "B3", "ZZ1", "A100000", "ZX99998"])
        from sd in Spellings(dest)
        select (Op)new PasteOp(single ? sa : sa + ":" + sb, sd);

    // Starts in the top-left block, at the grid edges (where an insert must throw InvalidOperationException when it
    // would push a Corner cell out), and outside the grid or with count 0 (R62: ArgumentOutOfRangeException).
    private static readonly Gen<Op> StructureGen =
        from kind in Gen.Elements("IR", "DR", "IC", "DC")
        from start in kind[1] == 'R'
            ? Gen.Elements(1, 1, 2, 2, 3, 99999, 100000, 0, 100001)
            : Gen.Elements(1, 1, 2, 2, 3, 701, 702, 0, 703)
        from count in Gen.Elements(1, 1, 1, 2, 3, 0)
        select (Op)new StructureOp(kind, start, count);

    private static readonly string[] NamePool = ["Rate", "rate", "Total", "x_1"];

    private static readonly Gen<Op> NameGen = Gen.OneOf(
        from n in Gen.Elements(NamePool)
        from r in Gen.Elements("$A$1", "$b$2", "$A$1:$C$2", "$C$2:$A$1", "$B$1:$B$1", "$ZZ$100000", "$ZY$99999:$ZZ$100000",
            "#REF!", "A1") // the last two are rejected (R59, R58)
        select (Op)new DefineOp(n, r),
        Gen.Elements(NamePool).Select(n => (Op)new DeleteNameOp(n)));

    private static readonly Gen<Op> OpGen = Gen.Frequency(
        (5, from a in AddressGen from i in InputGen select (Op)new SetOp(a, i)),
        (3, EditGen.ArrayOf().Select(e => (Op)new BatchOp(e))),
        (4, PasteGen),
        (4, StructureGen),
        (2, NameGen),
        (1, Gen.Choose(1, 70).Select(n => (Op)new BurstOp(n))),
        (4, Gen.Constant((Op)new UndoOp())),
        (2, Gen.Constant((Op)new RedoOp())),
        (1, Gen.Constant((Op)new MarkSavedOp())));

    private static readonly Gen<(KeyValuePair<string, string>[] Initial, Op[] Ops)> ScenarioGen =
        from init in Gen.Elements([.. Pool, .. Corner]).SelectMany(a => InputGen.Select(i => new KeyValuePair<string, string>(a, i))).ArrayOf()
        from ops in OpGen.ArrayOf()
        select (init.DistinctBy(p => p.Key).ToArray(), ops);

    /// <summary>Defined names as GetNames reports them (definition casing and stored refersTo), keyed
    /// case-insensitively. Also the §5.5 casing resolver for the structure model.</summary>
    private sealed class NameState(Dictionary<string, KeyValuePair<string, string>> byName) : INameCasingResolver
    {
        public Dictionary<string, KeyValuePair<string, string>> ByName { get; } = byName;

        public KeyValuePair<string, string>[] Sorted() =>
            [.. ByName.Values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)];

        public bool SameAs(IEnumerable<KeyValuePair<string, string>> names) => Sorted().SequenceEqual(names);

        public string? GetDefinedCasing(string name) => ByName.TryGetValue(name, out var p) ? p.Key : null;

        public NameState With(string name, string? refersTo)
        {
            var next = new Dictionary<string, KeyValuePair<string, string>>(ByName, StringComparer.OrdinalIgnoreCase);
            next.Remove(name);
            if (refersTo is not null) next.Add(name, new(name, refersTo));
            return new NameState(next);
        }
    }

    private static NameState NamesOf(Workbook wb) =>
        new(wb.GetNames().ToDictionary(p => p.Key, p => p, StringComparer.OrdinalIgnoreCase));

    /// <summary>A naive reference model of the history: every state snapshot (canonical address to non-empty input,
    /// plus the names) in a list, a cursor, and step ids.</summary>
    private sealed class Model
    {
        public List<Dictionary<string, string>> States { get; } = [];
        public List<NameState> Names { get; } = [];
        public List<long> Ids { get; } = [];
        public int Cursor { get; set; }
        public long SavedId { get; set; }
        public long NextId { get; set; } = 1;

        public Dictionary<string, string> Current => States[Cursor];
        public NameState CurrentNames => Names[Cursor];
        public bool Dirty => Ids[Cursor] != SavedId;

        public void Push(Dictionary<string, string> state, NameState? names = null)
        {
            var nextNames = names ?? CurrentNames;
            States.RemoveRange(Cursor + 1, States.Count - Cursor - 1);
            Names.RemoveRange(Cursor + 1, Names.Count - Cursor - 1);
            Ids.RemoveRange(Cursor + 1, Ids.Count - Cursor - 1);
            States.Add(state);
            Names.Add(nextNames);
            Ids.Add(NextId++);
            Cursor++;
            if (States.Count - 1 > Workbook.UndoLimit)
            {
                States.RemoveAt(0);
                Names.RemoveAt(0);
                Ids.RemoveAt(0);
                Cursor--;
            }
        }
    }

    private static Dictionary<string, string> Inputs(Workbook wb) =>
        wb.GetUsedAddresses().ToDictionary(a => a, wb.GetInput);

    private static bool SameInputs(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && string.Equals(p.Value, v, StringComparison.Ordinal));

    /// <summary>Values match a fresh load of the same inputs and names (§6.7: incremental recalc equals a full one).</summary>
    private static bool ValuesMatchFresh(Workbook wb)
    {
        var used = wb.GetUsedAddresses();
        var fresh = Workbook.FromCells(used.Select(a => new KeyValuePair<string, string>(a, wb.GetInput(a))), wb.GetNames());
        return used.Concat(Pool).All(a => wb.GetValue(a).Equals(fresh.GetValue(a)));
    }

    private static bool Check(Workbook wb, Model m) =>
        SameInputs(Inputs(wb), m.Current) && m.CurrentNames.SameAs(wb.GetNames()) && wb.IsDirty == m.Dirty
        && wb.CanUndo == (m.Cursor > 0) && wb.CanRedo == (m.Cursor < m.States.Count - 1) && ValuesMatchFresh(wb);

    private static Dictionary<string, string> Canon(Dictionary<string, string> state, string address, string input)
    {
        var next = new Dictionary<string, string>(state);
        string key = CellAddress.Parse(address).ToString();
        if (input.Length == 0) next.Remove(key);
        else next[key] = input;
        return next;
    }

    private static readonly FunctionArityAdapter Arities = new(BuiltInFunctionTable.Default);

    /// <summary>The §9.4 model of a paste over the whole state, or null when the destination exceeds the grid.
    /// Source cells are read from <paramref name="state"/> (the snapshot), so overlap needs no special care.</summary>
    private static Dictionary<string, string>? ModelPaste(Dictionary<string, string> state, NameState names, string source, string destination)
    {
        var range = CellRange.TryParse(source, out var r) ? r : new CellRange(CellAddress.Parse(source), CellAddress.Parse(source));
        var dest = CellAddress.Parse(destination);
        int dRow = dest.Row - range.TopLeft.Row, dCol = dest.Column - range.TopLeft.Column;
        if (range.BottomRight.Row + dRow > Workbook.MaxRows || range.BottomRight.Column + dCol > Workbook.MaxColumns) return null;
        var target = new CellRange(dest, new CellAddress(range.BottomRight.Column + dCol, range.BottomRight.Row + dRow));
        var next = new Dictionary<string, string>(state);
        foreach (var a in state.Keys)
            if (target.Contains(CellAddress.Parse(a))) next.Remove(a);
        foreach (var (a, input) in state)
        {
            var from = CellAddress.Parse(a);
            if (!range.Contains(from)) continue;
            string pasted = input; // constants and unparseable formulas verbatim (R39)
            if (input.StartsWith('=') && FormulaParser.Parse(input[1..], Arities).Root is { } root)
                pasted = FormulaPrinter.Print(root, new ReferenceShifter(dRow, dCol), names); // §5.5 name casing
            next[new CellAddress(from.Column + dCol, from.Row + dRow).ToString()] = pasted;
        }
        return next;
    }

    /// <summary>The §9.5 model of a structure edit, built on the per-coordinate rules of StructureRewriter (tested on
    /// their own): every input moves, a formula is re-emitted only when a reference changed (others, and #PARSE!
    /// formulas, verbatim), and every name target is adjusted. Returns the exception type the call must throw, or null
    /// with the next state.</summary>
    private static Type? ModelStructure(Dictionary<string, string> state, NameState names, StructureOp op,
        out Dictionary<string, string> next, out NameState nextNames)
    {
        next = state;
        nextNames = names;
        bool rows = op.Kind[1] == 'R', insert = op.Kind[0] == 'I';
        int bound = rows ? Workbook.MaxRows : Workbook.MaxColumns;
        if (op.Start < 1 || op.Start > bound || op.Count < 1 || (!insert && (long)op.Start + op.Count - 1 > bound))
            return typeof(ArgumentOutOfRangeException);
        var map = new StructureRewriter(rows ? StructureAxis.Rows : StructureAxis.Columns, insert, op.Start, op.Count);
        var moved = new Dictionary<string, string>();
        foreach (var (a, input) in state)
        {
            var cell = CellAddress.Parse(a);
            int to = map.MapPosition(map.PositionOf(cell));
            if (to < 0)
            {
                if (insert) return typeof(InvalidOperationException);
                continue;
            }
            string text = input;
            if (input.StartsWith('=') && FormulaParser.Parse(input[1..], Arities).Root is { } root)
            {
                map.Reset();
                string printed = FormulaPrinter.Print(root, map, names);
                if (map.Changed) text = printed;
            }
            moved[map.With(cell, to).ToString()] = text;
        }
        var adjusted = names;
        foreach (var (name, refersTo) in names.ByName.Values)
        {
            string after = refersTo;
            if (refersTo != "#REF!")
            {
                var parts = refersTo.Split(':');
                var tl = CellAddress.Parse(parts[0]);
                var br = CellAddress.Parse(parts[^1]);
                if (parts.Length == 1)
                {
                    int to = map.MapPosition(map.PositionOf(tl));
                    after = to < 0 ? "#REF!" : Abs(map.With(tl, to));
                }
                else
                {
                    var span = map.MapSpan(map.PositionOf(tl), map.PositionOf(br));
                    after = span is { } sp ? Abs(map.With(tl, sp.Lo)) + ":" + Abs(map.With(br, sp.Hi)) : "#REF!";
                }
            }
            if (after != refersTo) adjusted = adjusted.With(name, after);
        }
        next = moved;
        nextNames = adjusted;
        return null;
    }

    private static string Abs(CellAddress a) => new CellReference(a, ColumnAbsolute: true, RowAbsolute: true).ToString();

    private static void Structure(Workbook wb, StructureOp op)
    {
        switch (op.Kind)
        {
            case "IR": wb.InsertRows(op.Start, op.Count); break;
            case "DR": wb.DeleteRows(op.Start, op.Count); break;
            case "IC": wb.InsertColumns(op.Start, op.Count); break;
            default: wb.DeleteColumns(op.Start, op.Count); break;
        }
    }

    private static bool Run(KeyValuePair<string, string>[] initial, Op[] ops) => Run(initial, [], ops);

    private static bool Run(KeyValuePair<string, string>[] initial, KeyValuePair<string, string>[] initialNames, Op[] ops)
    {
        var wb = Workbook.FromCells(initial, initialNames);
        var m = new Model();
        m.States.Add(Inputs(wb));
        m.Names.Add(NamesOf(wb));
        m.Ids.Add(0);
        var initialInputs = Inputs(wb);
        var initialNameList = wb.GetNames();
        int events = 0;
        wb.CellsChanged += (_, _) => events++;
        if (!Check(wb, m)) return false;

        int burst = 0;
        foreach (var op in ops)
        {
            int eventsBefore = events;
            int expectedEvents;
            bool atMostOne = false; // the event depends on values only: a step that changes names but no input
            switch (op)
            {
                case SetOp s:
                {
                    var next = Canon(m.Current, s.Address, s.Input);
                    bool changes = !SameInputs(next, m.Current);
                    wb.SetCell(s.Address, s.Input);
                    if (changes) m.Push(next);
                    expectedEvents = changes ? 1 : 0;
                    break;
                }
                case BatchOp b:
                {
                    var next = m.Current;
                    foreach (var (a, i) in b.Edits) next = Canon(next, a, i);
                    bool changes = !SameInputs(next, m.Current);
                    wb.ApplyBatch(b.Edits);
                    if (changes) m.Push(next);
                    expectedEvents = changes ? 1 : 0;
                    break;
                }
                case PasteOp p:
                {
                    var next = ModelPaste(m.Current, m.CurrentNames, p.Source, p.Destination);
                    if (next is null)
                    {
                        try
                        {
                            wb.CopyPaste(p.Source, p.Destination);
                            return false; // must throw: the destination exceeds the grid
                        }
                        catch (ArgumentException) { }
                        expectedEvents = 0;
                        break;
                    }
                    bool changes = !SameInputs(next, m.Current);
                    wb.CopyPaste(p.Source, p.Destination);
                    if (changes) m.Push(next);
                    else if (wb.LastRecalc.EvaluatedFormulaCount != 0) return false; // no-op paste (R24)
                    expectedEvents = changes ? 1 : 0;
                    break;
                }
                case StructureOp so:
                {
                    var expected = ModelStructure(m.Current, m.CurrentNames, so, out var next, out var nextNames);
                    if (expected is not null)
                    {
                        try
                        {
                            Structure(wb, so);
                            return false; // must throw, changing nothing (checked below)
                        }
                        catch (Exception ex) when (ex.GetType() == expected) { }
                        expectedEvents = 0;
                        break;
                    }
                    bool inputsChange = !SameInputs(next, m.Current);
                    bool namesChange = !nextNames.SameAs(m.CurrentNames.Sorted());
                    Structure(wb, so);
                    if (inputsChange || namesChange) m.Push(next, nextNames);
                    else if (wb.LastRecalc.EvaluatedFormulaCount != 0) return false; // R63 no-op
                    expectedEvents = inputsChange ? 1 : 0; // R64: an input change is always reported
                    atMostOne = !inputsChange && namesChange;
                    break;
                }
                case DefineOp d:
                {
                    if (!NameSyntax.TryParseRefersTo(d.RefersTo, allowDeleted: false, out string canonical, out _))
                    {
                        try
                        {
                            wb.DefineName(d.Name, d.RefersTo);
                            return false;
                        }
                        catch (ArgumentException) { }
                        expectedEvents = 0;
                        break;
                    }
                    bool same = m.CurrentNames.ByName.TryGetValue(d.Name, out var cur)
                        && cur.Key == d.Name && cur.Value == canonical; // R60 no-op
                    wb.DefineName(d.Name, d.RefersTo);
                    if (!same) m.Push(m.Current, m.CurrentNames.With(d.Name, canonical));
                    expectedEvents = 0;
                    atMostOne = !same;
                    break;
                }
                case DeleteNameOp dn:
                {
                    if (!m.CurrentNames.ByName.ContainsKey(dn.Name))
                    {
                        try
                        {
                            wb.DeleteName(dn.Name);
                            return false;
                        }
                        catch (ArgumentException) { }
                        expectedEvents = 0;
                        break;
                    }
                    wb.DeleteName(dn.Name);
                    m.Push(m.Current, m.CurrentNames.With(dn.Name, null));
                    expectedEvents = 0;
                    atMostOne = true;
                    break;
                }
                case BurstOp u:
                {
                    for (int k = 0; k < u.Count; k++)
                    {
                        string input = "burst" + burst++;
                        wb.SetCell("C2", input);
                        m.Push(Canon(m.Current, "C2", input));
                    }
                    expectedEvents = u.Count; // one per SetCell
                    break;
                }
                case UndoOp:
                {
                    bool can = m.Cursor > 0;
                    if (wb.Undo() != can) return false;
                    if (can) m.Cursor--;
                    // A step that changed an input always reports, even when the values come out identical.
                    expectedEvents = can && !SameInputs(m.States[m.Cursor], m.States[m.Cursor + 1]) ? 1 : 0;
                    atMostOne = can && expectedEvents == 0;
                    break;
                }
                case RedoOp:
                {
                    bool can = m.Cursor < m.States.Count - 1;
                    if (wb.Redo() != can) return false;
                    if (can) m.Cursor++;
                    expectedEvents = can && !SameInputs(m.States[m.Cursor], m.States[m.Cursor - 1]) ? 1 : 0;
                    atMostOne = can && expectedEvents == 0;
                    break;
                }
                case MarkSavedOp:
                    expectedEvents = 0;
                    wb.MarkSaved();
                    m.SavedId = m.Ids[m.Cursor];
                    break;
                default:
                    throw new InvalidOperationException();
            }
            int raised = events - eventsBefore;
            if (atMostOne ? raised > 1 : raised != expectedEvents) return false;
            if (!Check(wb, m)) return false;
        }

        // Undo-all reaches the oldest kept state: the initial inputs and names unless steps were dropped (UndoLimit).
        while (wb.Undo()) m.Cursor--;
        if (m.Cursor != 0 || !Check(wb, m)) return false;
        bool dropped = m.Ids[0] != 0;
        if (!dropped && (!SameInputs(Inputs(wb), initialInputs) || !wb.GetNames().SequenceEqual(initialNameList))) return false;
        var fresh = Workbook.FromCells(m.States[0], m.Names[0].Sorted());
        if (!m.States[0].Keys.Concat(Pool).All(a => wb.GetValue(a).Equals(fresh.GetValue(a)))) return false;

        // Redo-all reaches the newest state.
        while (wb.Redo()) m.Cursor++;
        return m.Cursor == m.States.Count - 1 && Check(wb, m);
    }

    [Fact]
    public void RandomSequences_UndoAllRestoresInitial_RedoAllRestoresFinal_DirtyTracksPosition()
    {
        FsCheck.Check.One(Config.QuickThrowOnFailure.WithMaxTest(300),
            Prop.ForAll(ScenarioGen.ToArbitrary(), s => Run(s.Initial, s.Ops)));
    }

    [Fact]
    public void FixedLongSequence_CoversTheUndoLimitDrop()
    {
        Op[] ops = [new SetOp("A1", "=B1+1"), new MarkSavedOp(), new BurstOp(60), new SetOp("B1", "=A1"),
            new UndoOp(), new BurstOp(60), new UndoOp(), new RedoOp(), new BatchOp([new("A2", "=SUM(A1:C2)"), new("a2", "=A2")])];
        Assert.True(Run([new("B1", "4"), new("C1", "=A1*2")], ops));
    }

    [Fact]
    public void FixedPasteSequence_OverlapGridEdgesAndUndo()
    {
        Op[] ops = [new PasteOp("A1:B2", "B1"), new PasteOp("B1:C2", "A1"), new PasteOp("a1:c1", "A2"), new PasteOp("A2:C2", "a1"),
            new PasteOp("A1:C2", "ZX99999"), new PasteOp("$ZZ$100000:ZY99999", "A1"), new PasteOp("A1:B2", "ZZ1"),
            new PasteOp("A1:B1", "A100000"), new PasteOp("C2", "ZZ100000"), new UndoOp(), new UndoOp(), new RedoOp(),
            new PasteOp("A1:C2", "A1"), new PasteOp("A1:A2", "A100000")];
        Assert.True(Run([new("A1", "1"), new("B1", "=A1+$A$1"), new("C1", "=  sum(A1:B$1)"), new("A2", "=C2"), new("C2", "'t"),
            new("ZZ100000", "=SUM(ZY99999:ZZ99999)")], ops));
    }

    [Fact]
    public void FixedStructureSequence_NamesRefTargetsGridEdgesAndUndo()
    {
        Op[] ops = [new DefineOp("Rate", "$B$2"), new DefineOp("Total", "$C$2:$A$1"), new StructureOp("IR", 2, 2),
            new StructureOp("IC", 1, 1), new StructureOp("DR", 4, 1), new StructureOp("DC", 3, 2), new UndoOp(), new RedoOp(),
            new StructureOp("IR", 1, 1), new StructureOp("IR", 100000, 1), new StructureOp("DR", 1, 3), new DeleteNameOp("rate"),
            new UndoOp(), new StructureOp("DC", 0, 1), new StructureOp("DR", 99999, 3), new StructureOp("IC", 702, 1),
            new StructureOp("DR", 2, 1), new UndoOp(), new UndoOp(), new RedoOp(), new DefineOp("x_1", "$ZZ$100000")];
        Assert.True(Run([new("A1", "1"), new("B2", "= a1 + rate"), new("C2", "=SUM(Total)"), new("A3", "=SUM(C2:A1)"),
            new("C1", "=SUM("), new("B1", "'t")], [new("Edge", "$ZZ$100000"), new("Gone", "#REF!")], ops));
    }
}
