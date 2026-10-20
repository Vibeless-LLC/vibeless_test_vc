using System.Diagnostics.CodeAnalysis;
using Gridline.Core.Evaluation;
using Gridline.Core.Functions;

namespace Gridline.Core.Tests;

// Requirement IDs: §9.1 (Phase 2 members), §9.2 (atomicity, CellsChanged, LastRecalc), §9.3 (undo steps, redo,
// UndoLimit, dirty tracking, ApplyBatch), R24 (no-op), R25 (subscriber exceptions).
public class WorkbookUndoTests
{
    private static CellValue N(double d) => CellValue.FromNumber(d);
    private static KeyValuePair<string, string> P(string a, string i) => new(a, i);

    private sealed class EventLog
    {
        public List<string[]> Events { get; } = [];
        public EventLog(Workbook wb) => wb.CellsChanged += (sender, e) =>
        {
            Assert.Same(wb, sender);
            Events.Add([.. e.Addresses]);
        };
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

    private static Dictionary<string, (string Input, CellValue Value)> Snapshot(Workbook wb) =>
        wb.GetUsedAddresses().ToDictionary(a => a, a => (wb.GetInput(a), wb.GetValue(a)));

    // ---- new / loaded workbooks ----

    [Fact]
    public void NewWorkbook_IsCleanWithNoHistory()
    {
        var wb = new Workbook();
        Assert.False(wb.CanUndo);
        Assert.False(wb.CanRedo);
        Assert.False(wb.IsDirty);
        var log = new EventLog(wb);
        Assert.False(wb.Undo());
        Assert.False(wb.Redo());
        Assert.Empty(log.Events);
        Assert.False(wb.IsDirty);
    }

    [Fact]
    public void FromCells_IsCleanWithNoHistory()
    {
        var wb = Workbook.FromCells([P("A1", "1"), P("B1", "=A1+1")]);
        Assert.False(wb.CanUndo);
        Assert.False(wb.CanRedo);
        Assert.False(wb.IsDirty);
        Assert.False(wb.Undo());
        Assert.Equal(N(2), wb.GetValue("B1"));
    }

    [Fact]
    public void UndoRedo_WithNothingToDo_ReturnFalseAndKeepLastRecalc()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("B1", "=A1*2");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.False(wb.Redo());
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);

        Assert.True(wb.Undo());
        Assert.True(wb.Undo());
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        wb.SetCell("C1", "=1");
        Assert.True(wb.Undo()); // undoing C1 clears it: no formula left in the affected set
        var log = new EventLog(wb);
        var stats = wb.LastRecalc;
        Assert.False(wb.Undo());
        Assert.Equal(stats, wb.LastRecalc);
        Assert.Empty(log.Events);
    }

    // ---- one step per call; exact restore ----

    [Fact]
    public void SetCell_IsOneStep_UndoRestoresFormulaTextVerbatim_RedoReapplies()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "2");
        wb.SetCell("B1", "=  sum( a1 , 1 )");
        wb.SetCell("B1", "plain");
        Assert.True(wb.CanUndo);
        Assert.False(wb.CanRedo);

        Assert.True(wb.Undo());
        Assert.Equal("=  sum( a1 , 1 )", wb.GetInput("B1"));
        Assert.Equal(N(3), wb.GetValue("B1"));
        Assert.True(wb.CanRedo);

        // Edges came back: a precedent edit recalculates B1.
        wb.SetCell("A1", "5");
        Assert.Equal(N(6), wb.GetValue("B1"));
        Assert.False(wb.CanRedo); // cleared by the new mutation

        Assert.True(wb.Undo()); // A1 back to 2
        Assert.Equal("2", wb.GetInput("A1"));
        Assert.Equal(N(3), wb.GetValue("B1"));
        Assert.True(wb.Undo()); // B1 formula gone
        Assert.Equal("", wb.GetInput("B1"));
        Assert.Equal(CellValue.Blank, wb.GetValue("B1"));
        Assert.Equal(["A1"], wb.GetUsedAddresses());
        Assert.True(wb.Undo());
        Assert.Empty(wb.GetUsedAddresses());
        Assert.False(wb.CanUndo);

        Assert.True(wb.Redo());
        Assert.True(wb.Redo());
        Assert.Equal("=  sum( a1 , 1 )", wb.GetInput("B1"));
        Assert.Equal(N(3), wb.GetValue("B1"));
        Assert.True(wb.Redo());
        Assert.Equal("5", wb.GetInput("A1"));
        Assert.Equal(N(6), wb.GetValue("B1"));
        Assert.False(wb.Redo());
    }

    [Fact]
    public void NoOpSetCell_CreatesNoStep()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "");
        Assert.False(wb.CanUndo);
        Assert.False(wb.IsDirty);
        wb.SetCell("A1", "x");
        wb.MarkSaved();
        wb.SetCell("a1", "x");
        Assert.False(wb.IsDirty);
        Assert.True(wb.Undo());
        Assert.False(wb.CanUndo); // only the one real step existed
    }

    [Fact]
    public void NoOpCall_KeepsRedoStack()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("A1", "2");
        Assert.True(wb.Undo());
        wb.SetCell("A1", "1");           // same input: not a mutation
        wb.ApplyBatch([P("A1", "1")]);  // all edits dropped: not a mutation
        wb.ApplyBatch([]);
        Assert.True(wb.CanRedo);
        Assert.True(wb.Redo());
        Assert.Equal("2", wb.GetInput("A1"));
    }

    [Fact]
    public void NewMutationAfterUndo_ClearsRedo()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("A2", "2");
        Assert.True(wb.Undo());
        Assert.True(wb.CanRedo);
        wb.ApplyBatch([P("A3", "3")]);
        Assert.False(wb.CanRedo);
        Assert.False(wb.Redo());
        Assert.Equal("", wb.GetInput("A2"));
    }

    // ---- events and LastRecalc ----

    [Fact]
    public void UndoRedo_RaiseCellsChangedOnce_AndSetLastRecalc()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("B1", "=A1+1");
        wb.SetCell("C1", "=B1*10");
        wb.SetCell("D1", "7");
        wb.SetCell("A1", "5");
        Assert.Equal(N(60), wb.GetValue("C1"));
        var log = new EventLog(wb);

        Assert.True(wb.Undo());
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount); // B1, C1
        Assert.Equal(["A1", "B1", "C1"], Assert.Single(log.Events));
        Assert.Equal(N(20), wb.GetValue("C1"));

        Assert.True(wb.Undo()); // D1: constant, no dependents
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(["D1"], log.Events[1]);

        Assert.True(wb.Redo());
        Assert.True(wb.Redo());
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.Equal(4, log.Events.Count);
        Assert.Equal(["A1", "B1", "C1"], log.Events[3]);
        Assert.Equal(N(60), wb.GetValue("C1"));
    }

    [Fact]
    public void UndoWithIdenticalValues_StillRaisesForTouchedCells()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("B1", "=A1");
        wb.SetCell("A1", "1.0"); // same value, different input
        var log = new EventLog(wb);
        Assert.True(wb.Undo());
        Assert.Equal("1", wb.GetInput("A1"));
        Assert.Equal(["A1"], Assert.Single(log.Events)); // B1's value did not change
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
    }

    [Fact]
    public void ThrowingSubscriber_OnUndo_LeavesUndoCommitted()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("A1", "2");
        wb.CellsChanged += (_, _) => throw new InvalidOperationException("subscriber");
        Assert.Throws<InvalidOperationException>(() => wb.Undo());
        Assert.Equal("1", wb.GetInput("A1"));
        Assert.True(wb.CanRedo);
        Assert.Throws<InvalidOperationException>(() => wb.Redo());
        Assert.Equal("2", wb.GetInput("A1"));
        Assert.False(wb.CanRedo);
        Assert.Throws<InvalidOperationException>(() => wb.SetCell("A2", "x"));
        Assert.True(wb.CanUndo);
        Assert.Equal("x", wb.GetInput("A2"));
    }

    // ---- UndoLimit ----

    [Fact]
    public void HundredFirstStep_DropsTheOldest()
    {
        var wb = new Workbook();
        for (int i = 1; i <= Workbook.UndoLimit + 1; i++) wb.SetCell("A1", i.ToString());
        int undone = 0;
        while (wb.Undo()) undone++;
        Assert.Equal(Workbook.UndoLimit, undone);
        Assert.Equal("1", wb.GetInput("A1")); // the step that set "1" was dropped, so A1 cannot go back to blank
        int redone = 0;
        while (wb.Redo()) redone++;
        Assert.Equal(Workbook.UndoLimit, redone);
        Assert.Equal("101", wb.GetInput("A1"));
    }

    [Fact]
    public void ExactlyUndoLimitSteps_AreAllKept()
    {
        var wb = new Workbook();
        for (int i = 1; i <= Workbook.UndoLimit; i++) wb.SetCell("A1", i.ToString());
        while (wb.Undo()) { }
        Assert.Equal("", wb.GetInput("A1"));
        Assert.False(wb.IsDirty);
    }

    // ---- dirty tracking ----

    [Fact]
    public void Dirty_FollowsPositionRelativeToSaved()
    {
        var wb = new Workbook();
        wb.MarkSaved(); // harmless on a clean workbook
        Assert.False(wb.IsDirty);
        wb.SetCell("A1", "1");
        Assert.True(wb.IsDirty);
        wb.MarkSaved();
        Assert.False(wb.IsDirty);
        wb.SetCell("A1", "2");
        Assert.True(wb.IsDirty);
        Assert.True(wb.Undo());
        Assert.False(wb.IsDirty); // back at the saved position
        Assert.True(wb.Undo());
        Assert.True(wb.IsDirty);  // before the saved position
        Assert.True(wb.Redo());
        Assert.False(wb.IsDirty);
        Assert.True(wb.Redo());
        Assert.True(wb.IsDirty);
    }

    [Fact]
    public void Dirty_SameInputsByADifferentPath_IsStillDirty()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.MarkSaved();
        wb.SetCell("A1", "2");
        wb.SetCell("A1", "1"); // inputs equal the saved ones, but the position differs
        Assert.True(wb.IsDirty);
    }

    [Fact]
    public void Dirty_WhenSavedPositionWasDroppedFromHistory_UntilNextMarkSaved()
    {
        var wb = new Workbook();
        wb.MarkSaved(); // the initial position
        for (int i = 1; i <= Workbook.UndoLimit + 1; i++) wb.SetCell("A1", i.ToString());
        while (wb.Undo()) Assert.True(wb.IsDirty);
        Assert.True(wb.IsDirty); // the saved (initial) position is no longer reachable
        while (wb.Redo()) Assert.True(wb.IsDirty);
        wb.MarkSaved();
        Assert.False(wb.IsDirty);
        Assert.True(wb.Undo());
        Assert.True(wb.IsDirty);
        Assert.True(wb.Redo());
        Assert.False(wb.IsDirty);
    }

    [Fact]
    public void Dirty_WhenSavedPositionWasInClearedRedoStack_UntilNextMarkSaved()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("A1", "2");
        wb.MarkSaved();
        Assert.True(wb.Undo());
        Assert.True(wb.IsDirty);
        wb.SetCell("A1", "3"); // clears the redo stack that held the saved position
        Assert.True(wb.IsDirty);
        Assert.True(wb.Undo());
        Assert.True(wb.IsDirty);
        Assert.True(wb.Undo());
        Assert.True(wb.IsDirty);
        Assert.True(wb.Redo());
        wb.SetCell("A1", "2"); // same inputs as when saved, but a new position
        Assert.True(wb.IsDirty);
        wb.MarkSaved();
        Assert.False(wb.IsDirty);
    }

    // ---- ApplyBatch ----

    [Fact]
    public void ApplyBatch_IsOneStepOneRecalcOneEvent()
    {
        var wb = new Workbook();
        wb.SetCell("C1", "=A1+B1");
        wb.SetCell("D1", "=C1*2");
        var log = new EventLog(wb);
        wb.ApplyBatch([P("A1", "1"), P("B1", "2"), P("E1", "=A1")]);
        Assert.Equal(N(3), wb.GetValue("C1"));
        Assert.Equal(N(6), wb.GetValue("D1"));
        Assert.Equal(N(1), wb.GetValue("E1"));
        Assert.Equal(3, wb.LastRecalc.EvaluatedFormulaCount); // C1, D1, E1
        Assert.Equal(["A1", "B1", "C1", "D1", "E1"], Assert.Single(log.Events));

        Assert.True(wb.Undo()); // the whole batch at once
        Assert.Equal(["C1", "D1"], wb.GetUsedAddresses());
        Assert.Equal(N(0), wb.GetValue("C1"));
        Assert.Equal(["A1", "B1", "C1", "D1", "E1"], log.Events[1]);
        Assert.True(wb.Undo());
        Assert.True(wb.Undo());
        Assert.False(wb.CanUndo);
    }

    [Fact]
    public void ApplyBatch_LastEditWins_ComparedCanonically()
    {
        var wb = new Workbook();
        wb.ApplyBatch([P("a1", "1"), P("B1", "x"), P("$A$1", "2"), P("A1", "3")]);
        Assert.Equal("3", wb.GetInput("A1"));
        Assert.Equal("x", wb.GetInput("B1"));
        Assert.True(wb.Undo());
        Assert.Empty(wb.GetUsedAddresses());
    }

    [Fact]
    public void ApplyBatch_DuplicateEndingAtCurrentInput_IsANoOp()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.MarkSaved();
        var log = new EventLog(wb);
        wb.ApplyBatch([P("A1", "9"), P("A1", "1")]);
        Assert.Empty(log.Events);
        Assert.False(wb.IsDirty);
        Assert.True(wb.Undo());
        Assert.False(wb.CanUndo);
    }

    [Fact]
    public void ApplyBatch_DropsUnchangedEditsFromStepAndEvent()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        var log = new EventLog(wb);
        wb.ApplyBatch([P("A1", "1"), P("A2", "2")]);
        Assert.Equal(["A2"], Assert.Single(log.Events));
        Assert.True(wb.Undo());
        Assert.Equal(["A2"], log.Events[1]);
        Assert.Equal("1", wb.GetInput("A1"));
    }

    [Fact]
    public void ApplyBatch_Empty_IsANoOp()
    {
        var wb = new Workbook();
        wb.SetCell("B1", "=1");
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        var log = new EventLog(wb);
        wb.MarkSaved();
        wb.ApplyBatch([]);
        Assert.Empty(log.Events);
        Assert.Equal(0, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.False(wb.IsDirty);
        Assert.True(wb.Undo());
        Assert.False(wb.CanUndo);
    }

    [Theory]
    [InlineData("A0")]
    [InlineData("A1:B2")]
    [InlineData("")]
    [InlineData("AAA1")]
    [InlineData("A100001")]
    public void ApplyBatch_InvalidAddress_ThrowsAndAppliesNothing(string bad)
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        var log = new EventLog(wb);
        var stats = wb.LastRecalc;
        var ex = Assert.Throws<ArgumentException>(() => wb.ApplyBatch([P("A1", "2"), P("B1", "x"), P(bad, "3")]));
        Assert.Equal("edits", ex.ParamName);
        Assert.Equal("1", wb.GetInput("A1"));
        Assert.Equal(["A1"], wb.GetUsedAddresses());
        Assert.Empty(log.Events);
        Assert.Equal(stats, wb.LastRecalc);
        Assert.True(wb.Undo());
        Assert.False(wb.CanUndo);
    }

    [Fact]
    public void ApplyBatch_NullOrTooLong_ThrowsAndAppliesNothing()
    {
        var wb = new Workbook();
        Assert.Throws<ArgumentNullException>(() => wb.ApplyBatch(null!));
        Assert.ThrowsAny<ArgumentException>(() => wb.ApplyBatch([P("A1", "1"), P(null!, "2")]));
        Assert.ThrowsAny<ArgumentException>(() => wb.ApplyBatch([P("A1", "1"), P("A2", null!)]));
        Assert.ThrowsAny<ArgumentException>(() => wb.ApplyBatch([P("A1", "1"), default]));
        Assert.Throws<ArgumentException>(() => wb.ApplyBatch([P("A1", "1"), P("A2", new string('x', Workbook.MaxInputLength + 1))]));
        wb.ApplyBatch([P("A2", new string('x', Workbook.MaxInputLength))]);
        Assert.Equal(["A2"], wb.GetUsedAddresses());
        Assert.True(wb.Undo());
        Assert.False(wb.CanUndo);
    }

    [Fact]
    public void ApplyBatch_CyclesAndParseErrors()
    {
        var wb = new Workbook();
        wb.ApplyBatch([P("A1", "=B1"), P("B1", "=A1"), P("C1", "=C1"), P("D1", "=1+"), P("E1", "=A1+1")]);
        Assert.Equal(CellValue.FromError(ErrorCodes.Circ), wb.GetValue("A1"));
        Assert.Equal(CellValue.FromError(ErrorCodes.Circ), wb.GetValue("B1"));
        Assert.Equal(CellValue.FromError(ErrorCodes.Circ), wb.GetValue("C1"));
        Assert.Equal(CellValue.FromError(ErrorCodes.Parse), wb.GetValue("D1"));
        Assert.Equal(CellValue.FromError(ErrorCodes.Circ), wb.GetValue("E1"));
        Assert.Equal(5, wb.LastRecalc.EvaluatedFormulaCount);
        wb.ApplyBatch([P("B1", "4"), P("C1", "")]);
        Assert.Equal(N(4), wb.GetValue("A1"));
        Assert.Equal(N(5), wb.GetValue("E1"));
        Assert.True(wb.Undo());
        Assert.Equal(CellValue.FromError(ErrorCodes.Circ), wb.GetValue("A1"));
        Assert.Equal(CellValue.FromError(ErrorCodes.Circ), wb.GetValue("C1"));
        Assert.Equal(CellValue.FromError(ErrorCodes.Circ), wb.GetValue("E1"));
    }

    // ---- atomicity when evaluation throws (§9.2) ----

    [Fact]
    public void ApplyBatch_ThrowDuringRecalc_RollsBackEverything()
    {
        var resolver = new ArmableResolver();
        var wb = new Workbook(resolver);
        wb.SetCell("A1", "1");
        wb.SetCell("B1", "=A1+1");
        wb.SetCell("C1", "=B1*2");
        var before = Snapshot(wb);
        var stats = wb.LastRecalc;
        var log = new EventLog(wb);
        resolver.Armed = true; // B1 and C1 are rewritten before D1 throws
        Assert.Throws<InvalidOperationException>(() =>
            wb.ApplyBatch([P("A1", "5"), P("B1", "=A1+2"), P("D1", "=BOOM(C1)"), P("E1", "new")]));
        resolver.Armed = false;
        Assert.Empty(log.Events);
        Assert.Equal(before, Snapshot(wb));
        Assert.Equal(stats, wb.LastRecalc);
        Assert.False(wb.CanRedo);
        Assert.True(wb.Undo()); // only the three SetCell steps exist
        Assert.True(wb.Undo());
        Assert.True(wb.Undo());
        Assert.False(wb.CanUndo);
        // Edges intact after rollback and undo: rebuild and edit.
        Assert.True(wb.Redo());
        Assert.True(wb.Redo());
        Assert.True(wb.Redo());
        wb.SetCell("A1", "10");
        Assert.Equal(N(22), wb.GetValue("C1"));
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount); // D1's edge to C1 was rolled back
    }

    [Fact]
    public void UndoAndRedo_ThrowDuringRecalc_ChangeNothing()
    {
        var resolver = new ArmableResolver();
        var wb = new Workbook(resolver);
        wb.SetCell("A1", "1");
        wb.SetCell("B1", "=BOOM(A1)");
        wb.SetCell("B1", "2");
        wb.SetCell("C1", "=B1+1");
        wb.MarkSaved();
        Assert.True(wb.Undo()); // C1 gone
        var log = new EventLog(wb);

        resolver.Armed = true;
        var before = Snapshot(wb);
        var stats = wb.LastRecalc;
        Assert.Throws<InvalidOperationException>(() => wb.Undo()); // would restore =BOOM(A1)
        Assert.Equal(before, Snapshot(wb));
        Assert.Equal(stats, wb.LastRecalc);
        Assert.Empty(log.Events);
        Assert.True(wb.CanUndo);
        Assert.True(wb.CanRedo);
        Assert.True(wb.IsDirty);

        // Redo of C1 does not touch BOOM, so it works while armed and returns to the saved position.
        Assert.True(wb.Redo());
        Assert.False(wb.IsDirty);
        Assert.True(wb.Undo());

        resolver.Armed = false;
        Assert.True(wb.Undo());
        Assert.Equal("=BOOM(A1)", wb.GetInput("B1"));
        Assert.Equal(N(1), wb.GetValue("B1"));
        Assert.True(wb.Undo()); // B1 cleared
        resolver.Armed = true;
        before = Snapshot(wb);
        Assert.Throws<InvalidOperationException>(() => wb.Redo()); // would reapply =BOOM(A1)
        Assert.Equal(before, Snapshot(wb));
        Assert.Equal("", wb.GetInput("B1"));
        resolver.Armed = false;
        Assert.True(wb.Redo());
        Assert.Equal(N(1), wb.GetValue("B1"));
        wb.SetCell("A1", "7"); // edges intact after the failed redo
        Assert.Equal(N(7), wb.GetValue("B1"));
    }
}
