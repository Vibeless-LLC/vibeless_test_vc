using FsCheck;
using FsCheck.Fluent;

namespace Gridline.Core.Tests;

// Requirement IDs: §14 (undo/redo property: a random operation sequence followed by undo-all returns the initial
// inputs), §9.3 (one step per mutating call, no-op calls, redo clearing, UndoLimit drop, dirty tracking), §6.7.
public class UndoPropertyTests
{
    private static readonly string[] Pool = ["A1", "B1", "C1", "A2", "B2", "C2"];

    private abstract record Op;
    private sealed record SetOp(string Address, string Input) : Op;
    private sealed record BatchOp(KeyValuePair<string, string>[] Edits) : Op;
    private sealed record BurstOp(int Count) : Op; // many distinct SetCell steps, to reach the UndoLimit drop
    private sealed record UndoOp : Op;
    private sealed record RedoOp : Op;
    private sealed record MarkSavedOp : Op;

    private static readonly Gen<string> InputGen = Gen.OneOf(
        Gen.Choose(-50, 50).Select(n => n.ToString()),
        Gen.Elements("1.0", "0", "-0", "1e3", "abc", "", "", "TRUE", "false", "'=quoted"),
        Gen.Elements(
            "=A1+B1", "=SUM(A1:C2)", "=A2*2", "=B2&\"x\"", "=IF(C1>0,A1,B1)", "=C2", "=1/0", "=  a1 + 1",
            "=A1", "=B1", "=C1", "=A2",          // self- and mutual cycles depending on where they land
            "=SUM(A1:B1)", "=1+", "=SUM(", "=)", "=FOO(1)"));

    private static readonly Gen<string> AddressGen =
        Gen.Elements(Pool).SelectMany(a => Gen.Elements(a, a.ToLowerInvariant(), "$" + a[0] + "$" + a[1..]));

    private static readonly Gen<KeyValuePair<string, string>> EditGen =
        from a in AddressGen
        from i in InputGen
        select new KeyValuePair<string, string>(a, i);

    private static readonly Gen<Op> OpGen = Gen.Frequency(
        (5, from a in AddressGen from i in InputGen select (Op)new SetOp(a, i)),
        (3, EditGen.ArrayOf().Select(e => (Op)new BatchOp(e))),
        (1, Gen.Choose(1, 70).Select(n => (Op)new BurstOp(n))),
        (4, Gen.Constant((Op)new UndoOp())),
        (2, Gen.Constant((Op)new RedoOp())),
        (1, Gen.Constant((Op)new MarkSavedOp())));

    private static readonly Gen<(KeyValuePair<string, string>[] Initial, Op[] Ops)> ScenarioGen =
        from init in Gen.Elements(Pool).SelectMany(a => InputGen.Select(i => new KeyValuePair<string, string>(a, i))).ArrayOf()
        from ops in OpGen.ArrayOf()
        select (init.DistinctBy(p => p.Key).ToArray(), ops);

    /// <summary>A naive reference model of the history: every state snapshot in a list, a cursor, and step ids.</summary>
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
        Pool.ToDictionary(a => a, wb.GetInput);

    private static bool SameInputs(Dictionary<string, string> a, Dictionary<string, string> b) =>
        Pool.All(p => string.Equals(a[p], b[p], StringComparison.Ordinal));

    /// <summary>Values match a fresh FromCells of the same inputs (§6.7: incremental recalc equals a full one).</summary>
    private static bool ValuesMatchFresh(Workbook wb)
    {
        var fresh = Workbook.FromCells(Pool.Select(a => new KeyValuePair<string, string>(a, wb.GetInput(a))));
        return Pool.All(a => wb.GetValue(a).Equals(fresh.GetValue(a)));
    }

    private static bool Check(Workbook wb, Model m) =>
        SameInputs(Inputs(wb), m.Current) && wb.IsDirty == m.Dirty
        && wb.CanUndo == (m.Cursor > 0) && wb.CanRedo == (m.Cursor < m.States.Count - 1) && ValuesMatchFresh(wb);

    private static Dictionary<string, string> Canon(Dictionary<string, string> state, string address, string input)
    {
        var next = new Dictionary<string, string>(state)
        {
            [Gridline.Core.Addressing.CellAddress.Parse(address).ToString()] = input
        };
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
        var fresh = Workbook.FromCells(m.States[0].Where(p => p.Value.Length != 0).Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));
        if (!Pool.All(a => wb.GetValue(a).Equals(fresh.GetValue(a)))) return false;

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
}
