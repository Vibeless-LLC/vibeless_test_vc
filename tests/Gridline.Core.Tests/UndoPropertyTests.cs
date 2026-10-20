using FsCheck;
using FsCheck.Fluent;
using Gridline.Core.Addressing;
using Gridline.Core.Commands;
using Gridline.Core.Functions;
using Gridline.Core.Parsing;

namespace Gridline.Core.Tests;

// Requirement IDs: §14 (undo/redo property: a random operation sequence followed by undo-all returns the initial
// inputs), §9.3 (one step per mutating call, no-op calls, redo clearing, UndoLimit drop, dirty tracking), §9.4
// (CopyPaste: snapshot, overlap, blank clears, shifting, grid-edge rejection, one step), §6.7.
public class UndoPropertyTests
{
    private static readonly string[] Pool = ["A1", "B1", "C1", "A2", "B2", "C2"];
    private static readonly string[] Corner = ["ZY99999", "ZZ99999", "ZY100000", "ZZ100000"]; // bottom-right grid corner

    private abstract record Op;
    private sealed record SetOp(string Address, string Input) : Op;
    private sealed record BatchOp(KeyValuePair<string, string>[] Edits) : Op;
    private sealed record BurstOp(int Count) : Op; // many distinct SetCell steps, to reach the UndoLimit drop
    private sealed record PasteOp(string Source, string Destination) : Op;
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
            "=$A$1+B$2", "=SUM($A1:C$2)", "=zz100000", "=SUM(ZY99999:$ZZ$100000)")); // absolute, mixed, grid edge

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

    private static readonly Gen<Op> OpGen = Gen.Frequency(
        (5, from a in AddressGen from i in InputGen select (Op)new SetOp(a, i)),
        (3, EditGen.ArrayOf().Select(e => (Op)new BatchOp(e))),
        (4, PasteGen),
        (1, Gen.Choose(1, 70).Select(n => (Op)new BurstOp(n))),
        (4, Gen.Constant((Op)new UndoOp())),
        (2, Gen.Constant((Op)new RedoOp())),
        (1, Gen.Constant((Op)new MarkSavedOp())));

    private static readonly Gen<(KeyValuePair<string, string>[] Initial, Op[] Ops)> ScenarioGen =
        from init in Gen.Elements([.. Pool, .. Corner]).SelectMany(a => InputGen.Select(i => new KeyValuePair<string, string>(a, i))).ArrayOf()
        from ops in OpGen.ArrayOf()
        select (init.DistinctBy(p => p.Key).ToArray(), ops);

    /// <summary>A naive reference model of the history: every state snapshot (canonical address to non-empty input)
    /// in a list, a cursor, and step ids.</summary>
    private sealed class Model
    {
        public List<Dictionary<string, string>> States { get; } = [];
        public List<long> Ids { get; } = [];
        public int Cursor { get; set; }
        public long SavedId { get; set; }
        public long NextId { get; set; } = 1;

        public Dictionary<string, string> Current => States[Cursor];
        public bool Dirty => Ids[Cursor] != SavedId;

        public void Push(Dictionary<string, string> state)
        {
            States.RemoveRange(Cursor + 1, States.Count - Cursor - 1);
            Ids.RemoveRange(Cursor + 1, Ids.Count - Cursor - 1);
            States.Add(state);
            Ids.Add(NextId++);
            Cursor++;
            if (States.Count - 1 > Workbook.UndoLimit)
            {
                States.RemoveAt(0);
                Ids.RemoveAt(0);
                Cursor--;
            }
        }
    }

    private static Dictionary<string, string> Inputs(Workbook wb) =>
        wb.GetUsedAddresses().ToDictionary(a => a, wb.GetInput);

    private static bool SameInputs(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && string.Equals(p.Value, v, StringComparison.Ordinal));

    /// <summary>Values match a fresh FromCells of the same inputs (§6.7: incremental recalc equals a full one).</summary>
    private static bool ValuesMatchFresh(Workbook wb)
    {
        var used = wb.GetUsedAddresses();
        var fresh = Workbook.FromCells(used.Select(a => new KeyValuePair<string, string>(a, wb.GetInput(a))));
        return used.Concat(Pool).All(a => wb.GetValue(a).Equals(fresh.GetValue(a)));
    }

    private static bool Check(Workbook wb, Model m) =>
        SameInputs(Inputs(wb), m.Current) && wb.IsDirty == m.Dirty
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
    private static Dictionary<string, string>? ModelPaste(Dictionary<string, string> state, string source, string destination)
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
                pasted = FormulaPrinter.Print(root, new ReferenceShifter(dRow, dCol));
            next[new CellAddress(from.Column + dCol, from.Row + dRow).ToString()] = pasted;
        }
        return next;
    }

    private static bool Run(KeyValuePair<string, string>[] initial, Op[] ops)
    {
        var wb = Workbook.FromCells(initial);
        var m = new Model();
        m.States.Add(Inputs(wb));
        m.Ids.Add(0);
        var initialInputs = Inputs(wb);
        int events = 0;
        wb.CellsChanged += (_, _) => events++;
        if (!Check(wb, m)) return false;

        int burst = 0;
        foreach (var op in ops)
        {
            int eventsBefore = events;
            int expectedEvents;
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
                    var next = ModelPaste(m.Current, p.Source, p.Destination);
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
                    expectedEvents = can ? 1 : 0; // even when the values come out identical
                    break;
                }
                case RedoOp:
                {
                    bool can = m.Cursor < m.States.Count - 1;
                    if (wb.Redo() != can) return false;
                    if (can) m.Cursor++;
                    expectedEvents = can ? 1 : 0;
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
            if (events - eventsBefore != expectedEvents) return false;
            if (!Check(wb, m)) return false;
        }

        // Undo-all reaches the oldest kept state: the initial inputs unless steps were dropped (UndoLimit).
        while (wb.Undo()) m.Cursor--;
        if (m.Cursor != 0 || !Check(wb, m)) return false;
        bool dropped = m.Ids[0] != 0;
        if (!dropped && !SameInputs(Inputs(wb), initialInputs)) return false;
        var fresh = Workbook.FromCells(m.States[0]);
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
}
