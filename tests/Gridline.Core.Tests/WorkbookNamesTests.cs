using System.Diagnostics.CodeAnalysis;
using Gridline.Core.Evaluation;
using Gridline.Core.Functions;

namespace Gridline.Core.Tests;

// Requirement IDs: §9.6 (name syntax, refersTo, defining/deleting, evaluation), §9.1 (DefineName, DeleteName,
// GetNames), §9.2 (atomicity, CellsChanged, LastRecalc), §9.3 (one undo step, exact prior names), §6.7 (incremental
// recalc, cycles through names), §5.5 (name casing in rewritten formulas), §5.1/R57, R58, R59, R60, P1, P2.
public class WorkbookNamesTests
{
    private static CellValue N(double d) => CellValue.FromNumber(d);
    private static CellValue E(string code) => CellValue.FromError(code);
    private static KeyValuePair<string, string> P(string a, string b) => new(a, b);

    private sealed class EventLog
    {
        public List<string[]> Events { get; } = [];
        public EventLog(Workbook wb) => wb.CellsChanged += (_, e) => Events.Add([.. e.Addresses]);
    }

    /// <summary>Built-ins plus BOOM(x): returns x, but throws while <see cref="Armed"/>.</summary>
    private sealed class ArmableResolver : IFunctionResolver
    {
        public bool Armed { get; set; }

        private sealed class Boom(ArmableResolver owner) : IFormulaFunction
        {
            public CellValue Invoke(IFunctionArguments arguments) =>
                owner.Armed ? throw new InvalidOperationException("boom") : arguments.Evaluate(0);
        }

        public bool TryResolve(string upperName, [NotNullWhen(true)] out IFormulaFunction? function)
        {
            if (upperName == "BOOM") { function = new Boom(this); return true; }
            return BuiltInFunctionTable.Default.TryResolve(upperName, out function);
        }
    }

    // ---- Name syntax (§9.6) ----

    [Theory]
    [InlineData("Rate")]
    [InlineData("x")]
    [InlineData("_")]
    [InlineData("_x")]
    [InlineData("a.b")]
    [InlineData("my_name.x1")]
    [InlineData("abcd1")]   // four letters: does not look like a reference
    [InlineData("A1B")]
    [InlineData("A1.5")]
    [InlineData("R1C1")]
    [InlineData("SUM")]
    [InlineData("TRUEX")]
    [InlineData("_A1")]
    public void ValidNames_AreAccepted(string name)
    {
        var wb = new Workbook();
        wb.DefineName(name, "$A$1");
        Assert.Equal([P(name, "Sheet1!$A$1")], wb.GetNames());
    }

    [Fact]
    public void NameOf255Characters_IsAccepted_256_IsRejected()
    {
        var wb = new Workbook();
        string ok = "N" + new string('x', 254);
        wb.DefineName(ok, "$A$1");
        Assert.Equal(ok, Assert.Single(wb.GetNames()).Key);
        Assert.Throws<ArgumentException>(() => wb.DefineName(ok + "x", "$A$1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1a")]
    [InlineData(".a")]
    [InlineData("a b")]
    [InlineData("a-b")]
    [InlineData("a$b")]
    [InlineData("$A")]
    [InlineData("TRUE")]
    [InlineData("true")]
    [InlineData("False")]
    [InlineData("A1")]
    [InlineData("a1")]
    [InlineData("ABC123")]
    [InlineData("zzz9")]
    [InlineData("XFD1")]
    [InlineData("A0")]
    [InlineData("Rate\n")]
    [InlineData("café")]
    [InlineData("x١")] // Arabic-Indic digit: not [0-9]
    public void InvalidNames_ThrowArgumentException_AndChangeNothing(string name)
    {
        var wb = new Workbook();
        var log = new EventLog(wb);
        var ex = Assert.Throws<ArgumentException>(() => wb.DefineName(name, "$A$1"));
        Assert.Equal("name", ex.ParamName);
        Assert.Empty(wb.GetNames());
        Assert.False(wb.CanUndo);
        Assert.False(wb.IsDirty);
        Assert.Empty(log.Events);
    }

    [Fact]
    public void Names_AreUniqueCaseInsensitively_AndRedefinitionKeepsTheNewCasing()
    {
        var wb = new Workbook();
        wb.DefineName("Rate", "$A$1");
        wb.DefineName("RATE", "$B$2");
        Assert.Equal([P("RATE", "Sheet1!$B$2")], wb.GetNames());
        wb.DefineName("rate", "$B$2"); // casing-only change is a change (R60)
        Assert.Equal([P("rate", "Sheet1!$B$2")], wb.GetNames());
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var wb = new Workbook();
        Assert.Throws<ArgumentNullException>(() => wb.DefineName(null!, "$A$1"));
        Assert.Throws<ArgumentNullException>(() => wb.DefineName("x", null!));
        Assert.Throws<ArgumentNullException>(() => wb.DeleteName(null!));
        Assert.False(wb.CanUndo);
    }

    // ---- refersTo (§9.6, R58, R59) ----

    [Theory]
    [InlineData("$B$1", "Sheet1!$B$1")] // qualified with the active sheet (CR1.4, R77)
    [InlineData("$a$1", "Sheet1!$A$1")]
    [InlineData("$zz$100000", "Sheet1!$ZZ$100000")]
    [InlineData("$A$1:$C$10", "Sheet1!$A$1:$C$10")]
    [InlineData("$C$10:$A$1", "Sheet1!$A$1:$C$10")]
    [InlineData("$c$1:$a$10", "Sheet1!$A$1:$C$10")] // corners normalised top-left:bottom-right
    [InlineData("$A$1:$A$1", "Sheet1!$A$1:$A$1")]   // stays a range
    public void RefersTo_IsNormalisedToCanonicalUppercase(string refersTo, string stored)
    {
        var wb = new Workbook();
        wb.DefineName("x", refersTo);
        Assert.Equal([P("x", stored)], wb.GetNames());
    }

    [Theory]
    [InlineData("")]
    [InlineData("B1")]
    [InlineData("$B1")]
    [InlineData("B$1")]
    [InlineData("$A$1:B2")]
    [InlineData("$A$1:$B2")]
    [InlineData("A1:B2")]
    [InlineData("#REF!")] // R59: only the loader may store it
    [InlineData("#ref!")]
    [InlineData(" $A$1")]
    [InlineData("$A$1 ")]
    [InlineData("$A $1")]
    [InlineData("=$A$1")]
    [InlineData("$A$0")]
    [InlineData("$A$01")]
    [InlineData("$AAA$1")]
    [InlineData("$A$100001")]
    [InlineData("$A$1:")]
    [InlineData(":$A$1")]
    [InlineData("$A$1:$B$2:$C$3")]
    [InlineData("Rate")]
    [InlineData("$A$1,$B$2")]
    public void InvalidRefersTo_ThrowsArgumentException_AndChangesNothing(string refersTo)
    {
        var wb = new Workbook();
        wb.DefineName("Rate", "$A$1");
        var log = new EventLog(wb);
        var ex = Assert.Throws<ArgumentException>(() => wb.DefineName("Rate", refersTo));
        Assert.Equal("refersTo", ex.ParamName);
        Assert.Equal([P("Rate", "Sheet1!$A$1")], wb.GetNames());
        Assert.Empty(log.Events);
        wb.Undo();
        Assert.False(wb.CanUndo); // only the original definition was recorded
    }

    // ---- GetNames (§9.1) ----

    [Fact]
    public void GetNames_IsSortedOrdinalIgnoreCase_InDefinitionCasing_AndIsASnapshot()
    {
        var wb = new Workbook();
        wb.DefineName("beta", "$A$1");
        wb.DefineName("_z", "$A$2");
        wb.DefineName("Alpha", "$A$3:$B$4");
        wb.DefineName("ALPHA2", "$A$5");
        wb.DefineName("a.b", "$A$6");
        var names = wb.GetNames();
        string[] expected = ["beta", "_z", "Alpha", "ALPHA2", "a.b"];
        Array.Sort(expected, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected, names.Select(p => p.Key));
        Assert.Equal("Sheet1!$A$3:$B$4", names.Single(p => p.Key == "Alpha").Value);

        wb.DeleteName("beta");
        Assert.Equal(5, names.Count);
        Assert.Equal(4, wb.GetNames().Count);
        Assert.Throws<NotSupportedException>(() => ((IList<KeyValuePair<string, string>>)names).Add(P("x", "$A$1")));
    }

    [Fact]
    public void GetNames_IsEmptyForANewWorkbook() => Assert.Empty(new Workbook().GetNames());

    // ---- Evaluation (§9.6, §7.0, P2) ----

    [Fact]
    public void CellName_BehavesLikeTheCellReference()
    {
        var wb = new Workbook();
        wb.SetCell("B1", "5");
        wb.DefineName("Rate", "$B$1");
        wb.SetCell("A1", "=Rate*2");
        wb.SetCell("A2", "=rate+RATE");
        Assert.Equal(N(10), wb.GetValue("A1"));
        Assert.Equal(N(10), wb.GetValue("A2"));
        wb.SetCell("B1", "7"); // an edit inside the target reaches the name's dependents
        Assert.Equal(N(14), wb.GetValue("A1"));
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("B1", "");
        Assert.Equal(N(0), wb.GetValue("A1")); // §6.6 blank target
    }

    [Fact]
    public void RangeName_IsAReferenceArgument_AndValueInScalarContext()
    {
        var wb = new Workbook();
        wb.ApplyBatch([P("A1", "1"), P("A2", "text"), P("A3", "TRUE"), P("A4", "2"), P("A5", "'3")]);
        wb.DefineName("Total", "$A$1:$A$5");
        wb.SetCell("C1", "=SUM(Total)");
        wb.SetCell("C2", "=Total");
        wb.SetCell("C3", "=Total+1");
        wb.SetCell("C4", "=SUM((Total),10)");
        wb.SetCell("C5", "=COUNT(Total)");
        Assert.Equal(N(3), wb.GetValue("C1")); // text, booleans and numeric text ignored (§7.0 reference row)
        Assert.Equal(E(ErrorCodes.Value), wb.GetValue("C2")); // §6.5
        Assert.Equal(E(ErrorCodes.Value), wb.GetValue("C3"));
        Assert.Equal(N(13), wb.GetValue("C4"));
        Assert.Equal(N(2), wb.GetValue("C5"));
        wb.SetCell("A3", "=#N/A");
        Assert.Equal(E(ErrorCodes.NA), wb.GetValue("C1")); // reference errors propagate
        wb.SetCell("A3", "");
        wb.SetCell("B9", "100"); // outside the range: no recalc of C1
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
    }

    // §9.6, §5.1 (Task 1 review ruling): an undefined name where a reference is required gives #NAME?, not #VALUE!.
    [Fact]
    public void ReferenceOnlyBuiltIns_ThroughNames()
    {
        var wb = new Workbook();
        wb.ApplyBatch([P("A1", "1"), P("A3", "=\"\""), P("C1", "=COUNTBLANK(Area)"), P("C2", "=ISBLANK(Spot)"),
            P("C3", "=ISBLANK(Area)"), P("C4", "=COUNTBLANK(Spot)")]);
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("C1"));
        Assert.Equal(CellValue.FromBoolean(false), wb.GetValue("C2")); // ISBLANK never errors (§7.2)
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("C4"));

        wb.DefineName("Area", "$A$1:$A$4");
        wb.DefineName("Spot", "$B$1");
        Assert.Equal(N(3), wb.GetValue("C1")); // A2, A3 ("" result) and A4
        Assert.Equal(CellValue.FromBoolean(true), wb.GetValue("C2"));
        Assert.Equal(CellValue.FromBoolean(false), wb.GetValue("C3")); // multi-cell range
        Assert.Equal(N(1), wb.GetValue("C4"));
        wb.SetCell("B1", "x");
        Assert.Equal(CellValue.FromBoolean(false), wb.GetValue("C2"));
        Assert.Equal(N(0), wb.GetValue("C4"));
    }

    [Fact]
    public void SingleCellRangeName_IsStillARange()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "4");
        wb.DefineName("One", "$A$1:$A$1");
        wb.SetCell("B1", "=One");
        wb.SetCell("B2", "=SUM(One)");
        Assert.Equal(E(ErrorCodes.Value), wb.GetValue("B1"));
        Assert.Equal(N(4), wb.GetValue("B2"));
    }

    [Fact]
    public void UndefinedName_IsName_ResolvesOnDefine_AndReturnsOnDelete()
    {
        var wb = new Workbook();
        wb.SetCell("B1", "5");
        wb.SetCell("A1", "=Rate*2");
        wb.SetCell("A2", "=SUM(Rate)");
        wb.SetCell("A3", "=A1+1");
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("A1"));
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("A2"));

        var log = new EventLog(wb);
        wb.DefineName("RATE", "$B$1");
        Assert.Equal(N(10), wb.GetValue("A1"));
        Assert.Equal(N(5), wb.GetValue("A2"));
        Assert.Equal(N(11), wb.GetValue("A3"));
        Assert.Equal(3, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(["A1", "A2", "A3"], Assert.Single(log.Events));
        Assert.Equal("=Rate*2", wb.GetInput("A1")); // R60: inputs are never rewritten

        wb.DeleteName("rate"); // case-insensitive
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("A1"));
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("A2"));
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("A3"));
        Assert.Equal(["A1", "A2", "A3"], log.Events[1]);
        Assert.Empty(wb.GetNames());
        wb.SetCell("B1", "6"); // the deleted name's target is no longer a precedent
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void Redefinition_RecalculatesOnlyTheNamesDependents()
    {
        var wb = new Workbook();
        wb.ApplyBatch([P("B1", "1"), P("B2", "2"), P("C1", "=B1*100"), P("A1", "=Rate"), P("A2", "=A1*10")]);
        wb.DefineName("Rate", "$B$1");
        Assert.Equal(N(10), wb.GetValue("A2"));

        var log = new EventLog(wb);
        wb.DefineName("Rate", "$B$2");
        Assert.Equal(N(2), wb.GetValue("A1"));
        Assert.Equal(N(20), wb.GetValue("A2"));
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount); // A1 and A2, not C1 (§6.7)
        Assert.Equal(["A1", "A2"], Assert.Single(log.Events));

        wb.SetCell("B1", "9"); // old target: only C1 depends on it now
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(N(2), wb.GetValue("A1"));
        wb.SetCell("B2", "3");
        Assert.Equal(N(30), wb.GetValue("A2"));
    }

    [Fact]
    public void NameWithoutDependents_IsAStepWithoutAnEvent()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "=1");
        wb.MarkSaved();
        var log = new EventLog(wb);
        wb.DefineName("x", "$A$1");
        Assert.True(wb.CanUndo);
        Assert.True(wb.IsDirty);
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Empty(log.Events);
        Assert.True(wb.Undo());
        Assert.False(wb.IsDirty);
        Assert.Empty(log.Events);
    }

    [Fact]
    public void RedefinitionWithEqualValues_RaisesNoEvent()
    {
        var wb = new Workbook();
        wb.ApplyBatch([P("B1", "5"), P("B2", "5"), P("A1", "=Rate")]);
        wb.DefineName("Rate", "$B$1");
        var log = new EventLog(wb);
        wb.DefineName("Rate", "$B$2");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Empty(log.Events); // §9.2: no address changed
    }

    // ---- Cycles (§6.7) ----

    [Fact]
    public void CycleThroughAName_IsCirc_AndBreaksWhenTheNameIsDeleted()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "=Me+1");
        wb.SetCell("B1", "=IFERROR(A1,0)");
        wb.SetCell("B2", "=A1");
        wb.DefineName("Me", "$A$1");
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("A1"));
        Assert.Equal(N(0), wb.GetValue("B1"));
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("B2"));
        wb.DeleteName("Me");
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("A1"));
        Assert.Equal(N(0), wb.GetValue("B1"));
    }

    [Fact]
    public void CycleThroughARangeName_AndAMutualCycle_AreCirc()
    {
        var wb = new Workbook();
        wb.DefineName("Total", "$A$1:$A$3");
        wb.SetCell("A2", "=SUM(Total)");
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("A2"));

        wb.DefineName("Other", "$C$1");
        wb.SetCell("C1", "=Back");
        wb.SetCell("D1", "=Other");
        wb.DefineName("Back", "$D$1");
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("C1"));
        Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("D1"));
        wb.DefineName("Back", "$E$1");
        Assert.Equal(N(0), wb.GetValue("C1"));
        Assert.Equal(N(0), wb.GetValue("D1"));
    }

    // §6.7, R10: a 20,000-link chain where every link goes through a name recalculates on a small stack.
    [Fact]
    public void LongChainThroughNames_DoesNotRecurse()
    {
        const int n = 20_000;
        Evaluation.Eval.OnSmallStack(256 * 1024, () =>
        {
            var wb = new Workbook();
            for (int r = 1; r < n; r++) wb.DefineName("link_" + r, "$A$" + r);
            var edits = new List<KeyValuePair<string, string>>(n) { P("A1", "1") };
            for (int r = 2; r <= n; r++) edits.Add(P("A" + r, "=link_" + (r - 1) + "+1"));
            wb.ApplyBatch(edits);
            Assert.Equal(N(n), wb.GetValue("A" + n));
            wb.SetCell("A1", "2");
            Assert.Equal(n - 1, wb.LastRecalc.EvaluatedFormulaCount);
            Assert.Equal(N(n + 1), wb.GetValue("A" + n));
            wb.DefineName("link_1", "$A$2"); // a self-cycle on A2 makes the whole tail #CIRC!
            Assert.Equal(E(ErrorCodes.Circ), wb.GetValue("A" + n));
        });
    }

    // ---- Undo (§9.3, R60) ----

    [Fact]
    public void EachCall_IsOneUndoStep_RestoringTheExactNamesAndInputs()
    {
        var wb = new Workbook();
        wb.ApplyBatch([P("B1", "1"), P("B2", "2"), P("A1", "= rate *10")]);
        var s0 = wb.GetNames();
        wb.DefineName("rate", "$b$1");
        var s1 = wb.GetNames();
        wb.DefineName("RATE", "$B$2");
        var s2 = wb.GetNames();
        wb.DeleteName("Rate");
        Assert.Empty(wb.GetNames());
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("A1"));

        Assert.True(wb.Undo());
        Assert.Equal(s2, wb.GetNames());
        Assert.Equal(N(20), wb.GetValue("A1"));
        Assert.True(wb.Undo());
        Assert.Equal(s1, wb.GetNames());
        Assert.Equal([P("rate", "Sheet1!$B$1")], wb.GetNames());
        Assert.Equal(N(10), wb.GetValue("A1"));
        Assert.True(wb.Undo());
        Assert.Equal(s0, wb.GetNames());
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("A1"));
        Assert.Equal("= rate *10", wb.GetInput("A1")); // verbatim
        Assert.True(wb.Undo()); // the batch
        Assert.False(wb.CanUndo);

        Assert.True(wb.Redo());
        Assert.True(wb.Redo());
        Assert.Equal(s1, wb.GetNames());
        Assert.Equal(N(10), wb.GetValue("A1"));
        Assert.True(wb.Redo());
        Assert.Equal(s2, wb.GetNames());
        Assert.True(wb.Redo());
        Assert.Empty(wb.GetNames());
        Assert.False(wb.CanRedo);
    }

    [Fact]
    public void UndoOfANameStep_RaisesTheChangedDependents()
    {
        var wb = new Workbook();
        wb.ApplyBatch([P("B1", "4"), P("A1", "=Rate")]);
        wb.DefineName("Rate", "$B$1");
        var log = new EventLog(wb);
        wb.Undo();
        Assert.Equal(["A1"], Assert.Single(log.Events));
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        wb.Redo();
        Assert.Equal(N(4), wb.GetValue("A1"));
        Assert.Equal(2, log.Events.Count);
    }

    [Fact]
    public void IdenticalDefinition_IsANoOp_R60()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "=Rate");
        wb.DefineName("Rate", "$B$1");
        wb.SetCell("C1", "x");
        wb.Undo(); // leaves a redo step that a no-op must keep
        var log = new EventLog(wb);
        wb.DefineName("Rate", "$B$1");
        wb.DefineName("Rate", "$b$1"); // same canonical refersTo
        Assert.Empty(log.Events);
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.True(wb.CanRedo);
        Assert.True(wb.Undo()); // the original DefineName, not a no-op step
        Assert.Empty(wb.GetNames());
        Assert.True(wb.Undo());
        Assert.False(wb.CanUndo);
    }

    [Fact]
    public void CasingOnlyChange_IsAStep_ButReevaluatesNothing()
    {
        var wb = new Workbook();
        wb.ApplyBatch([P("B1", "4"), P("A1", "=Rate")]);
        wb.DefineName("Rate", "$B$1");
        wb.MarkSaved();
        var log = new EventLog(wb);
        wb.DefineName("RATE", "$B$1");
        Assert.True(wb.IsDirty);
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Empty(log.Events);
        Assert.Equal([P("RATE", "Sheet1!$B$1")], wb.GetNames());
        wb.Undo();
        Assert.Equal([P("Rate", "Sheet1!$B$1")], wb.GetNames());
        Assert.False(wb.IsDirty);
    }

    [Fact]
    public void DeleteName_Unknown_ThrowsAndChangesNothing()
    {
        var wb = new Workbook();
        wb.DefineName("Rate", "$A$1");
        var ex = Assert.Throws<ArgumentException>(() => wb.DeleteName("Other"));
        Assert.Equal("name", ex.ParamName);
        Assert.Throws<ArgumentException>(() => wb.DeleteName(""));
        Assert.Single(wb.GetNames());
        wb.Undo();
        Assert.False(wb.CanUndo);
    }

    [Fact]
    public void NewMutationAfterUndo_ClearsRedo()
    {
        var wb = new Workbook();
        wb.DefineName("a", "$A$1");
        wb.Undo();
        wb.DefineName("b", "$A$1");
        Assert.False(wb.CanRedo);
        Assert.Equal([P("b", "Sheet1!$A$1")], wb.GetNames());
    }

    // ---- Atomicity (§9.2) ----

    [Fact]
    public void ThrowingEvaluation_DuringDefineName_LeavesEverythingUnchanged()
    {
        var functions = new ArmableResolver();
        var wb = Workbook.FromCells([P("B1", "5"), P("B2", "7"), P("A1", "=BOOM(Rate)"), P("A2", "=Rate*2")], functions);
        wb.DefineName("Rate", "$B$1");
        Assert.Equal(N(5), wb.GetValue("A1"));
        var log = new EventLog(wb);

        functions.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.DefineName("RATE", "$B$2"));
        Assert.Throws<InvalidOperationException>(() => wb.DeleteName("Rate"));
        wb.DefineName("Other", "$B$2"); // no dependents, nothing evaluated: succeeds while armed
        functions.Armed = false;

        Assert.Equal([P("Other", "Sheet1!$B$2"), P("Rate", "Sheet1!$B$1")], wb.GetNames());
        Assert.Equal(N(5), wb.GetValue("A1"));
        Assert.Equal(N(10), wb.GetValue("A2"));
        Assert.Empty(log.Events);
        // The graph's name target rolled back too: B1 still drives the dependents, B2 does not.
        wb.SetCell("B2", "8");
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("B1", "6");
        Assert.Equal(N(12), wb.GetValue("A2"));
    }

    [Fact]
    public void ThrowingEvaluation_DuringUndoAndRedo_OfANameStep_LeavesEverythingUnchanged()
    {
        var functions = new ArmableResolver();
        var wb = Workbook.FromCells([P("B1", "5"), P("A1", "=BOOM(Rate)")], functions);
        wb.DefineName("Rate", "$B$1");

        functions.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.Undo());
        functions.Armed = false;
        Assert.Equal([P("Rate", "Sheet1!$B$1")], wb.GetNames());
        Assert.True(wb.CanUndo);
        Assert.False(wb.CanRedo);

        Assert.True(wb.Undo());
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("A1"));
        functions.Armed = true;
        Assert.Throws<InvalidOperationException>(() => wb.Redo());
        functions.Armed = false;
        Assert.Empty(wb.GetNames());
        Assert.True(wb.CanRedo);
        Assert.True(wb.Redo());
        Assert.Equal(N(5), wb.GetValue("A1"));
    }

    // ---- Lexer and printing (R57, §5.5) ----

    [Fact]
    public void UnderscoreNames_WorkInFormulas_R57()
    {
        var wb = new Workbook();
        wb.SetCell("B1", "3");
        wb.SetCell("A1", "=_x*2");
        wb.SetCell("A2", "=_x(1)");
        wb.SetCell("A3", "=_x$1");
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("A1"));
        Assert.Equal(E(ErrorCodes.Parse), wb.GetValue("A2"));
        Assert.Equal(E(ErrorCodes.Parse), wb.GetValue("A3"));
        wb.DefineName("_x", "$B$1");
        Assert.Equal(N(6), wb.GetValue("A1"));
    }

    [Fact]
    public void CopyPaste_PrintsNamesInTheirDefinitionCasing()
    {
        var wb = new Workbook();
        wb.DefineName("TaxRate", "$B$1");
        wb.SetCell("B1", "2");
        wb.SetCell("A1", "= taxrate * C1 + undefinedName");
        wb.CopyPaste("A1", "A2");
        Assert.Equal("=TaxRate*C2+undefinedName", wb.GetInput("A2"));
        Assert.Equal(E(ErrorCodes.Name), wb.GetValue("A2"));
        wb.DefineName("UndefinedName", "$C$2");
        wb.SetCell("C2", "5");
        Assert.Equal(N(15), wb.GetValue("A2"));
    }
}
