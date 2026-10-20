using System.Collections.Immutable;
using Gridline.Core.Addressing;
using Gridline.Core.Commands;
using Gridline.Core.Dependencies;

namespace Gridline.Core;

// The multi-cell mutation path shared by SetCell, ApplyBatch, Undo and Redo (§9.2, §9.3); the undo stack itself
// lives in Gridline.Core.Commands (UndoHistory, UndoStep).
public sealed partial class Workbook
{
    /// <summary>
    /// Applies several cell inputs as one mutation and one undo step (§9.1, §9.3): one recalculation with every
    /// edited cell in the changed set and at most one <see cref="CellsChanged"/>. If an address appears more than
    /// once the last edit wins; edits equal to the current input are dropped, and if none remain (including an empty
    /// batch) the call is a no-op with no step and no event (LastRecalc becomes 0, as for a no-op SetCell, R24).
    /// </summary>
    /// <exception cref="ArgumentException">The list or an address or input is null, an address is invalid or a
    /// range, or an input is longer than <see cref="MaxInputLength"/>. Nothing is applied.</exception>
    public void ApplyBatch(IReadOnlyList<KeyValuePair<string, string>> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var last = new Dictionary<int, (CellAddress Cell, string Input)>(edits.Count); // first-seen order, last value
        for (int i = 0; i < edits.Count; i++)
        {
            var (address, input) = edits[i];
            var cell = ParseAddress(address, nameof(edits));
            ValidateInput(input, nameof(edits));
            last[CellKey.Pack(cell)] = (cell, input);
        }

        var changes = ImmutableArray.CreateBuilder<CellEdit>(last.Count);
        foreach (var (key, (cell, input)) in last)
        {
            string current = _cells.TryGetValue(key, out var e) ? e.Input : "";
            if (!string.Equals(current, input, StringComparison.Ordinal)) changes.Add(new CellEdit(cell, current, input));
        }
        if (changes.Count == 0)
        {
            LastRecalc = default;
            return;
        }
        Perform(new UndoStep(changes.DrainToImmutable()));
    }

    /// <summary>True when there is a step to undo (§9.3).</summary>
    public bool CanUndo => _history.CanUndo;

    /// <summary>True when there is an undone step to redo (§9.3).</summary>
    public bool CanRedo => _history.CanRedo;

    /// <summary>True exactly when the history position differs from the one recorded by <see cref="MarkSaved"/>
    /// (§9.3). A new or loaded workbook is clean.</summary>
    public bool IsDirty => _history.IsDirty;

    /// <summary>Records the current history position as saved (§9.3).</summary>
    public void MarkSaved() => _history.MarkSaved();

    /// <summary>
    /// Restores the exact prior inputs of the newest step (§9.3), recalculates and raises <see cref="CellsChanged"/>
    /// once with every touched cell plus every formula whose value changed. Creates no step. Returns false, changing
    /// nothing (not even <see cref="LastRecalc"/>), when there is nothing to undo. Atomic on exceptions (§9.2).
    /// </summary>
    public bool Undo()
    {
        var step = _history.PeekUndo();
        if (step is null) return false;
        var result = Apply(step.CellEdits, forward: false);
        _history.CompleteUndo();
        Finish(step.CellEdits, result);
        return true;
    }

    /// <summary>Reapplies the newest undone step (§9.3); otherwise as <see cref="Undo"/>.</summary>
    public bool Redo()
    {
        var step = _history.PeekRedo();
        if (step is null) return false;
        var result = Apply(step.CellEdits, forward: true);
        _history.CompleteRedo();
        Finish(step.CellEdits, result);
        return true;
    }

    /// <summary>Applies a new step, records it (clearing redo, §9.3), then publishes the result.</summary>
    private void Perform(UndoStep step)
    {
        var result = Apply(step.CellEdits, forward: true);
        _history.Record(step);
        Finish(step.CellEdits, result);
    }

    /// <summary>Sets LastRecalc and raises CellsChanged once. Runs after the edit and the history are committed, so
    /// a throwing subscriber leaves the edit in place (R25).</summary>
    private void Finish(ImmutableArray<CellEdit> edits, RecalcResult result)
    {
        LastRecalc = new RecalcStats(result.EvaluatedFormulaCount);
        var handler = CellsChanged;
        if (handler is not null) handler(this, new CellsChangedEventArgs(ChangedAddresses(edits, result.ChangedCells)));
    }

    /// <summary>
    /// Sets every edited cell to its new input (<paramref name="forward"/>) or its old input, updates the edges and
    /// runs one recalculation with all edited cells in the changed set. Atomic (§9.2): on an exception every cell
    /// entry, edge and written value is restored and the exception propagates. Each address appears at most once.
    /// </summary>
    private RecalcResult Apply(ImmutableArray<CellEdit> edits, bool forward)
    {
        int n = edits.Length;
        // Pure; nothing is mutated yet. A formula keeps the cell's old value until recalc writes the new one, which is
        // what RecalcResult.ChangedCells compares against (RecalcResult contract).
        var olds = new Entry?[n];
        var news = new Entry?[n];
        for (int i = 0; i < n; i++)
        {
            string input = forward ? edits[i].NewInput : edits[i].OldInput;
            _cells.TryGetValue(CellKey.Pack(edits[i].Address), out olds[i]);
            news[i] = input.Length == 0 ? null : CreateEntry(input, olds[i]?.Value ?? CellValue.Blank);
        }
        CellAddress[] changed;
        if (n == 1)
        {
            _changedOne[0] = edits[0].Address;
            changed = _changedOne;
        }
        else
        {
            changed = new CellAddress[n];
            for (int i = 0; i < n; i++) changed[i] = edits[i].Address;
        }

        int touched = 0; // cells [0, touched) may have been mutated
        _rollback.Clear();
        _logWrites = true;
        try
        {
            for (int i = 0; i < n; i++)
            {
                touched = i + 1;
                SetEntry(edits[i].Address, olds[i], news[i]);
            }
            return _graph.Recalculate(changed, _target, _functions);
        }
        catch
        {
            for (int i = _rollback.Count - 1; i >= 0; i--) _rollback[i].Entry.Value = _rollback[i].OldValue;
            for (int i = touched - 1; i >= 0; i--) SetEntry(edits[i].Address, news[i], olds[i]);
            throw;
        }
        finally
        {
            _logWrites = false;
            if (_rollback.Capacity > RollbackRetainCapacity) _rollback = []; // release the big backing array
            else _rollback.Clear();
        }
    }

    /// <summary>Replaces a cell's entry <paramref name="from"/> → <paramref name="to"/> (null = empty) and its edges.
    /// Idempotent, so the rollback may also replay a cell whose forward replacement threw part-way.</summary>
    private void SetEntry(CellAddress cell, Entry? from, Entry? to)
    {
        if (to is null) _cells.Remove(CellKey.Pack(cell));
        else _cells[CellKey.Pack(cell)] = to;
        if (from?.Formula is not null || to?.Formula is not null) _graph.SetFormula(cell, to?.Formula);
    }
}
