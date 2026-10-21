using System.Text;
using FsCheck;
using FsCheck.Fluent;
using Gridline.Core.Addressing;

namespace Gridline.Core.Tests;

// Requirement IDs: §9.3 / §14 (undo of any sequence restores the exact prior inputs, names and sheet list; redo reapplies),
// CR1.3 (AddSheet, RenameSheet, DeleteSheet, MoveSheet, ActiveSheet, qualified SetCell and DefineName), §9.2 (atomic
// errors), §6.7 (incremental recalc equals a fresh load after every step), R78-R80, R85.
public class SheetUndoPropertyTests
{
    private abstract record Op;
    private sealed record AddOp(string Name, int? Index) : Op;
    private sealed record RenameOp(string Old, string New) : Op;
    private sealed record DeleteOp(string Name) : Op;
    private sealed record MoveOp(string Name, int Index) : Op;
    private sealed record SetOp(string Address, string Input) : Op;
    private sealed record DefineOp(string Name, string RefersTo) : Op;
    private sealed record ActivateOp(string Name) : Op;
    private sealed record DeleteNameOp(string Name) : Op;
    private sealed record ReemitOp(bool Paste) : Op; // InsertRows(1,1) or CopyPaste(A1:B2, A3) on the active sheet
    private sealed record UndoOp : Op;
    private sealed record RedoOp : Op;

    // Existing, missing, differently cased, quoted and invalid sheet names; "Gone" and "New" are referenced while missing.
    private static readonly string[] SheetPool = ["Sheet1", "Data", "data", "My Sheet", "O'k", "New", "NEW", "Gone", "DATA2", "bad:name"];

    private static readonly string[] Inputs =
    [
        "1", "2", "x", "", "", "=Data!A1+1", "=SUM('My Sheet'!A1:B2)", "=data!A1+DATA2!B1", "=Gone!A1", "=New!A1*2",
        "=Rate*2", "=SUMIF(Data!A1:A2,\">0\",'My Sheet'!B1)", "=SUMIF(Total,\">0\",New!B1)", "=A1+Sheet1!B1", "=1+",
        "=Data!A1+New!A1", "='O''k'!A1", "=COUNT(Gone!A1:B2,Data!A1:B2)", "=SUM(Total)", "=B1", "=  data!b2 * 2",
        "=Sheet1!A1+Sheet1!A2", "=IF(Data!A1>1,'My Sheet'!A2,Gone!B2)",
        // As-written casings that §5.5 re-emission canonicalises (names, functions, other-sheet prefixes)
        "=data!A1*rate", "=sum(data!A1:B2)+total", "=my sheet!A1", "='my sheet'!A1+data2!A1+gone!A1*RATE", "=if(new!A1,TOTAL,0)",
    ];

    private static readonly Gen<string> AddressGen =
        from sheet in Gen.Elements([null, null, .. SheetPool])
        from cell in Gen.Elements("A1", "B1", "A2", "B2")
        select sheet is null ? cell : SheetName.Format(SheetName.IsValid(sheet) ? sheet : "Data") + "!" + cell;

    private static readonly Gen<Op> OpGen = Gen.Frequency(
        (3, from n in Gen.Elements(SheetPool) from i in Gen.Elements<int?>(null, null, 0, 1, 2, 9) select (Op)new AddOp(n, i)),
        (4, from o in Gen.Elements(SheetPool) from n in Gen.Elements(SheetPool) select (Op)new RenameOp(o, n)),
        (2, Gen.Elements(SheetPool).Select(n => (Op)new DeleteOp(n))),
        (2, from n in Gen.Elements(SheetPool) from i in Gen.Choose(-1, 4) select (Op)new MoveOp(n, i)),
        (6, from a in AddressGen from i in Gen.Elements(Inputs) select (Op)new SetOp(a, i)),
        (2, from n in Gen.Elements("Rate", "Total") from r in Gen.Elements("$A$1", "Data!$A$1", "'My Sheet'!$A$1:$B$2", "new!$B$1", "Gone!$A$1")
            select (Op)new DefineOp(n, r)),
        (1, Gen.Elements(SheetPool).Select(n => (Op)new ActivateOp(n))),
        (1, Gen.Elements("Rate", "total").Select(n => (Op)new DeleteNameOp(n))),
        (2, Gen.Elements(false, true).Select(p => (Op)new ReemitOp(p))),
        (3, Gen.Constant((Op)new UndoOp())),
        (2, Gen.Constant((Op)new RedoOp())));

    private static readonly Gen<Op[]> ScenarioGen = OpGen.ArrayOf().Select(ops => ops.Take(60).ToArray());

    /// <summary>The sheet list, every input (per sheet, row-major) and the names: what undo restores (not activation, R80).</summary>
    private static string Snap(Workbook wb)
    {
        var sb = new StringBuilder(string.Join("|", wb.SheetNames)).Append('\n');
        foreach (var a in wb.GetUsedAddresses()) sb.Append(a).Append('=').Append(wb.GetInput(a)).Append('\n');
        foreach (var (n, r) in wb.GetNames()) sb.Append(n).Append(":=").Append(r).Append('\n');
        return sb.ToString();
    }

    /// <summary>A fresh load of the same sheets, inputs, names and active sheet.</summary>
    private static Workbook Fresh(Workbook wb)
    {
        var names = wb.SheetNames;
        var cells = names.ToDictionary(n => n, _ => new List<KeyValuePair<string, string>>(), StringComparer.OrdinalIgnoreCase);
        foreach (var a in wb.GetUsedAddresses())
        {
            Assert.True(SheetName.TrySplitQualified(a, out string? sheet, out string rest));
            cells[sheet ?? names[0]].Add(new(rest, wb.GetInput(a)));
        }
        return Workbook.FromSheets([.. names.Select(n => new LoadedSheet(n, cells[n]))], wb.ActiveSheet, wb.GetNames());
    }

    private static void Reemit(Workbook wb, ReemitOp op)
    {
        if (op.Paste) wb.CopyPaste("A1:B2", "A3");
        else wb.InsertRows(1, 1);
    }

    /// <summary>Every value equals that of a fresh load of the same sheets, inputs and names (§6.7).</summary>
    private static bool ValuesMatchFresh(Workbook wb)
    {
        var fresh = Fresh(wb);
        return wb.GetUsedAddresses().All(a => wb.GetValue(a).Equals(fresh.GetValue(a)));
    }

    private static void Apply(Workbook wb, Op op)
    {
        switch (op)
        {
            case AddOp a: wb.AddSheet(a.Name, a.Index); break;
            case RenameOp r: wb.RenameSheet(r.Old, r.New); break;
            case DeleteOp d: wb.DeleteSheet(d.Name); break;
            case MoveOp m: wb.MoveSheet(m.Name, m.Index); break;
            case SetOp s: wb.SetCell(s.Address, s.Input); break;
            case DefineOp d: wb.DefineName(d.Name, d.RefersTo); break;
            case ActivateOp a: wb.ActiveSheet = a.Name; break;
            case DeleteNameOp d: wb.DeleteName(d.Name); break;
            case ReemitOp r: Reemit(wb, r); break;
            default: throw new InvalidOperationException();
        }
    }

    private static bool Run(Op[] ops)
    {
        var wb = Workbook.FromSheets(
            [new LoadedSheet("Sheet1", [new("A1", "=Data!A1+'My Sheet'!B2")]),
             new LoadedSheet("Data", [new("A1", "3"), new("A2", "=New!A1")]),
             new LoadedSheet("My Sheet", [new("B2", "4")])],
            "Sheet1", [new("Rate", "Data!$A$1")]); // a loaded multi-sheet workbook with a clean history

        var states = new List<string> { Snap(wb) };
        int cursor = 0;
        foreach (var op in ops)
        {
            string before = Snap(wb);
            switch (op)
            {
                case UndoOp:
                    if (wb.Undo() != (cursor > 0)) return false;
                    if (cursor > 0) cursor--;
                    break;
                case RedoOp:
                    if (wb.Redo() != (cursor < states.Count - 1)) return false;
                    if (cursor < states.Count - 1) cursor++;
                    break;
                default:
                    bool canUndo = wb.CanUndo, canRedo = wb.CanRedo;
                    // §5.5: re-emission depends only on the stored inputs, so the live workbook (whatever its history)
                    // and a fresh load of the same state must produce the same inputs.
                    Workbook? fresh = op is ReemitOp ? Fresh(wb) : null;
                    try
                    {
                        Apply(wb, op);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                        // R78: every error leaves the workbook unchanged.
                        if (Snap(wb) != before || wb.CanUndo != canUndo || wb.CanRedo != canRedo) return false;
                        break;
                    }
                    string after = Snap(wb);
                    if (fresh is not null)
                    {
                        Reemit(fresh, (ReemitOp)op);
                        if (Snap(fresh) != after) return false;
                    }
                    if (after == before) // a no-op (R24, R60, R79) or activation: no step
                    {
                        if (wb.CanUndo != canUndo || wb.CanRedo != canRedo) return false;
                        break;
                    }
                    states.RemoveRange(cursor + 1, states.Count - cursor - 1);
                    states.Add(after);
                    cursor++;
                    if (wb.CanRedo) return false;
                    break;
            }
            if (Snap(wb) != states[cursor] || !wb.SheetNames.Contains(wb.ActiveSheet) || !ValuesMatchFresh(wb)) return false;
            WorkbookSheetChangesTests.AssertInstalledTreesMatchInputs(wb);
        }

        while (wb.Undo()) cursor--;
        if (cursor != 0 || Snap(wb) != states[0] || !ValuesMatchFresh(wb)) return false;
        while (wb.Redo()) cursor++;
        return cursor == states.Count - 1 && Snap(wb) == states[^1] && ValuesMatchFresh(wb);
    }

    [Fact]
    public void RandomSheetOperations_UndoRestoresEveryPriorState_RedoReapplies_ValuesMatchAFreshLoad()
    {
        FsCheck.Check.One(Config.QuickThrowOnFailure.WithMaxTest(300), Prop.ForAll(ScenarioGen.ToArbitrary(), Run));
    }

    [Fact]
    public void FixedSequence_RenameIntoMissingNames_DeleteRestoreAndCasing()
    {
        Op[] ops = [new RenameOp("Data", "New"), new SetOp("New!A1", "5"), new RenameOp("new", "NEW"), new DeleteOp("NEW"),
            new UndoOp(), new RenameOp("NEW", "Gone"), new MoveOp("Gone", 0), new DeleteOp("Sheet1"), new AddOp("Data", 0),
            new SetOp("Data!A1", "=Gone!A1+data!A2"), new UndoOp(), new UndoOp(), new RedoOp(), new DefineOp("Total", "Gone!$A$1"),
            new RenameOp("Gone", "My Sheet"), new RenameOp("Gone", "O'k"), new DeleteOp("O'k"), new UndoOp(), new RedoOp()];
        Assert.True(Run(ops));
    }

    [Fact]
    public void FixedSequence_RenameThenDeleteName_ReEmitsLikeAFreshLoad()
    {
        Op[] ops = [new SetOp("A1", "=data!A1*rate"), new SetOp("B1", "=sum(data!A1:B2)+total"), new DefineOp("Total", "Data!$A$1"),
            new RenameOp("Data", "Info"), new DeleteNameOp("Rate"), new DeleteNameOp("total"), new ReemitOp(true), new ReemitOp(false),
            new UndoOp(), new UndoOp(), new UndoOp(), new UndoOp(), new RedoOp(), new RedoOp(), new RedoOp(), new RedoOp()];
        Assert.True(Run(ops));
    }
}
