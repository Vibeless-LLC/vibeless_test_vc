namespace Gridline.Core.Commands;

/// <summary>
/// The undo and redo stacks plus the saved position (§9.3). Steps are opaque; the owner applies them and calls
/// <see cref="CompleteUndo"/> / <see cref="CompleteRedo"/> only after the application succeeded, so a throwing
/// undo or redo leaves the history unchanged (§9.2).
/// </summary>
/// <remarks>
/// Every recorded step gets a fresh id from a monotonic counter, and the history position is the id of the newest
/// applied step (the top of the undo stack), or the id of the last step dropped off the bottom when the undo stack
/// is empty (0 initially). Ids are never reused, so a saved position that was dropped from the bottom, or that sat
/// in a redo stack a new step cleared, can never equal the position again: the workbook stays dirty until the next
/// <see cref="MarkSaved"/>.
/// </remarks>
public sealed class UndoHistory
{
    private readonly int _limit;
    private readonly LinkedList<(long Id, UndoStep Step)> _undo = new(); // First = oldest, Last = newest
    private readonly Stack<(long Id, UndoStep Step)> _redo = new();
    private long _nextId = 1;
    private long _baseId;  // position when the undo stack is empty: id of the newest dropped step, or 0
    private long _savedId; // a new history is at its saved position

    /// <param name="limit">Maximum undo steps kept (§9.3: <c>Workbook.UndoLimit</c>).</param>
    public UndoHistory(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _limit = limit;
    }

    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    /// <summary>The current history position (see remarks).</summary>
    public long Position => _undo.Last is { } last ? last.Value.Id : _baseId;

    /// <summary>True exactly when <see cref="Position"/> differs from the saved position (§9.3).</summary>
    public bool IsDirty => Position != _savedId;

    /// <summary>Records the current position as saved (§9.3).</summary>
    public void MarkSaved() => _savedId = Position;

    /// <summary>Records a step that has just been applied: clears the redo stack and drops the oldest step when
    /// more than the limit would be kept (§9.3). O(1).</summary>
    public void Record(UndoStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _redo.Clear();
        _undo.AddLast((_nextId++, step));
        if (_undo.Count > _limit)
        {
            _baseId = _undo.First!.Value.Id;
            _undo.RemoveFirst();
        }
    }

    /// <summary>The step <see cref="CompleteUndo"/> would move to the redo stack, or null.</summary>
    public UndoStep? PeekUndo() => _undo.Last?.Value.Step;

    /// <summary>The step <see cref="CompleteRedo"/> would move back to the undo stack, or null.</summary>
    public UndoStep? PeekRedo() => _redo.TryPeek(out var top) ? top.Step : null;

    /// <summary>Moves the newest undo step to the redo stack, after the owner reverted it.</summary>
    public void CompleteUndo()
    {
        var last = _undo.Last ?? throw new InvalidOperationException("Nothing to undo.");
        _undo.RemoveLast();
        _redo.Push(last.Value);
    }

    /// <summary>Moves the newest redo step back to the undo stack, after the owner reapplied it. Redo never drops a
    /// step: the undo stack only regains the step it lost.</summary>
    public void CompleteRedo()
    {
        if (!_redo.TryPop(out var top)) throw new InvalidOperationException("Nothing to redo.");
        _undo.AddLast(top);
    }
}
