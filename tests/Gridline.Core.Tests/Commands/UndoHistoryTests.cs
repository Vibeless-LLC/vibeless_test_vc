using Gridline.Core.Addressing;
using Gridline.Core.Commands;

namespace Gridline.Core.Tests.Commands;

// Requirement IDs: §9.3 (undo/redo stacks, UndoLimit drop, saved position, dirty tracking).
public class UndoHistoryTests
{
    private static UndoStep Step(string input) => new([new CellEdit(CellAddress.Parse("A1"), "", input)]);

    [Fact]
    public void New_IsCleanAndEmpty()
    {
        var h = new UndoHistory(3);
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
        Assert.False(h.IsDirty);
        Assert.Equal(0, h.Position);
        Assert.Null(h.PeekUndo());
        Assert.Null(h.PeekRedo());
        Assert.Throws<InvalidOperationException>(h.CompleteUndo);
        Assert.Throws<InvalidOperationException>(h.CompleteRedo);
    }

    [Fact]
    public void Limit_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UndoHistory(0));
        Assert.Throws<ArgumentNullException>(() => new UndoHistory(1).Record(null!));
        Assert.Throws<ArgumentNullException>(() => new UndoStep(default));
    }

    // The apply/rollback path replaces each edited cell once; a duplicate address would corrupt rollback (§9.2, §9.3).
    [Fact]
    public void UndoStep_RejectsADuplicateAddress()
    {
        var a1 = CellAddress.Parse("A1");
        var ex = Assert.Throws<ArgumentException>(() =>
            new UndoStep([new CellEdit(a1, "", "1"), new CellEdit(CellAddress.Parse("B1"), "", "2"), new CellEdit(a1, "1", "3")]));
        Assert.Equal("cellEdits", ex.ParamName);
        Assert.Single(new UndoStep([new CellEdit(a1, "", "1")]).CellEdits);
        Assert.Empty(new UndoStep([]).CellEdits);
        Assert.Equal(2, new UndoStep([new CellEdit(a1, "", "1"), new CellEdit(CellAddress.Parse("A2"), "", "2")]).CellEdits.Length);
    }

    // §9.3, §9.6: a name step carries each changed name once (case-insensitively), and each edit has at least one side.
    [Fact]
    public void UndoStep_NameEdits_AreValidated()
    {
        var rate = new NameDefinition("Rate", "$A$1");
        var step = new UndoStep([], [new NameEdit(null, rate)]);
        Assert.Empty(step.CellEdits);
        Assert.Equal("Rate", Assert.Single(step.NameEdits).Name);
        Assert.Empty(new UndoStep([]).NameEdits);
        Assert.Equal("RATE", new NameEdit(rate, new NameDefinition("RATE", "$A$1")).Name);

        Assert.Throws<ArgumentNullException>(() => new UndoStep([], default));
        Assert.Throws<ArgumentException>(() => new UndoStep([], [new NameEdit(null, null)]));
        Assert.Throws<ArgumentException>(() => new UndoStep([], [new NameEdit(rate, new NameDefinition("Other", "$A$1"))]));
        var ex = Assert.Throws<ArgumentException>(() =>
            new UndoStep([], [new NameEdit(null, rate), new NameEdit(new NameDefinition("RATE", "$B$1"), null)]));
        Assert.Equal("nameEdits", ex.ParamName);
    }

    [Fact]
    public void PeekThenComplete_MovesStepsBetweenStacks()
    {
        var h = new UndoHistory(3);
        var a = Step("a");
        var b = Step("b");
        h.Record(a);
        h.Record(b);
        Assert.Same(b, h.PeekUndo());
        h.CompleteUndo();
        Assert.Same(a, h.PeekUndo());
        Assert.Same(b, h.PeekRedo());
        h.CompleteRedo();
        Assert.Same(b, h.PeekUndo());
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Record_DropsOldestBeyondLimit_AndSavedPositionBecomesUnreachable()
    {
        var h = new UndoHistory(2);
        var a = Step("a");
        h.Record(a);
        h.MarkSaved(); // after a
        h.Record(Step("b"));
        h.Record(Step("c")); // drops a; the position "after a" stays reachable as the base
        Assert.Equal(2, h.UndoCount);
        h.CompleteUndo();
        h.CompleteUndo();
        Assert.False(h.IsDirty);
        Assert.False(h.CanUndo);
        h.CompleteRedo();
        h.CompleteRedo();
        h.Record(Step("d")); // drops b: "after a" is gone
        while (h.CanUndo) { h.CompleteUndo(); Assert.True(h.IsDirty); }
        Assert.Equal(2, h.RedoCount); // c and d kept; a and b dropped
    }

    [Fact]
    public void Positions_AreNeverReused()
    {
        var h = new UndoHistory(5);
        h.Record(Step("a"));
        long afterA = h.Position;
        h.CompleteUndo();
        h.Record(Step("b"));
        Assert.NotEqual(afterA, h.Position);
        Assert.False(h.CanRedo);
    }
}
