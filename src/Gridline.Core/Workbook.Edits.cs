using System.Collections.Immutable;
using Gridline.Core.Addressing;
using Gridline.Core.Commands;
using Gridline.Core.Dependencies;

namespace Gridline.Core;

// The mutation path shared by SetCell, ApplyBatch, CopyPaste, DefineName, DeleteName, Undo and Redo (§9.2, §9.3; structure
// steps take their own path in Workbook.Structure.cs, sheet steps in Workbook.Sheets.cs); the undo stack itself
// lives in Gridline.Core.Commands (UndoHistory, UndoStep).
public sealed partial class Workbook
{
    /// <summary>
    /// Applies several cell inputs as one mutation and one undo step (§9.1, §9.3): one recalculation with every
    /// edited cell in the changed set and at most one <see cref="CellsChanged"/>. If an address appears more than
    /// once the last edit wins; edits equal to the current input are dropped, and if none remain (including an empty
    /// batch) the call is a no-op with no step and no event (LastRecalc becomes 0, as for a no-op SetCell, R24).
    /// </summary>
    /// <remarks>Addresses may be qualified (R83); duplicates compare the sheet and the canonical address, so <c>A1</c> and
    /// <c>Sheet1!A1</c> on active Sheet1 are the same cell.</remarks>
    /// <exception cref="ArgumentException">The list or an address or input is null, an address is invalid, a range or
    /// names an unknown sheet, or an input is longer than <see cref="MaxInputLength"/>. Nothing is applied.</exception>
    public void ApplyBatch(IReadOnlyList<KeyValuePair<string, string>> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var last = new Dictionary<long, (Sheet Sheet, CellAddress Cell, string Input)>(edits.Count); // first-seen order, last value
        for (int i = 0; i < edits.Count; i++)
        {
            var (address, input) = edits[i];
            var (sheet, cell) = ResolveAddress(address, nameof(edits));
            ValidateInput(input, nameof(edits));
            last[SheetCellKey.Pack(sheet.Id, cell)] = (sheet, cell, input);
        }

        var changes = ImmutableArray.CreateBuilder<CellEdit>(last.Count);
        foreach (var (sheet, cell, input) in last.Values)
        {
            string current = sheet.Cells.TryGetValue(CellKey.Pack(cell), out var e) ? e.Input : "";
            if (!string.Equals(current, input, StringComparison.Ordinal))
                changes.Add(new CellEdit(cell, current, input) { Sheet = sheet.Id });
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
    /// Restores the exact prior inputs and names of the newest step (§9.3), recalculates and raises
    /// <see cref="CellsChanged"/> once with every touched cell plus every formula whose value changed (not raised when
    /// that set is empty, which only a names-only step can produce). Creates no step. Returns false, changing nothing
    /// (not even <see cref="LastRecalc"/>), when there is nothing to undo. Atomic on exceptions (§9.2).
    /// </summary>
    public bool Undo()
    {
        var step = _history.PeekUndo();
        if (step is null) return false;
        if (step.SheetInsert is not null)
        {
            var (sheetResult, sheetChanged) = ApplySheetInsert(step, forward: false);
            _history.CompleteUndo();
            FinishSheetStep(sheetResult, sheetChanged);
            return true;
        }
        if (step.Structure is not null)
        {
            var (structureResult, changed) = ApplyStructure(step, forward: false);
            _history.CompleteUndo();
            FinishStructure(structureResult, changed);
            return true;
        }
        var result = Apply(step, forward: false);
        _history.CompleteUndo();
        Finish(step.CellEdits, result);
        return true;
    }

    /// <summary>Reapplies the newest undone step (§9.3); otherwise as <see cref="Undo"/>.</summary>
    public bool Redo()
    {
        var step = _history.PeekRedo();
        if (step is null) return false;
        if (step.SheetInsert is not null)
        {
            var (sheetResult, sheetChanged) = ApplySheetInsert(step, forward: true);
            _history.CompleteRedo();
            FinishSheetStep(sheetResult, sheetChanged);
            return true;
        }
        if (step.Structure is not null)
        {
            var (structureResult, changed) = ApplyStructure(step, forward: true);
            _history.CompleteRedo();
            FinishStructure(structureResult, changed);
            return true;
        }
        var result = Apply(step, forward: true);
        _history.CompleteRedo();
        Finish(step.CellEdits, result);
        return true;
    }

    /// <summary>Applies a new step, records it (clearing redo, §9.3), then publishes the result.</summary>
    private void Perform(UndoStep step)
    {
        var result = Apply(step, forward: true);
        _history.Record(step);
        Finish(step.CellEdits, result);
    }

    /// <summary>Sets LastRecalc and raises CellsChanged once, unless no address changed (§9.2, R60). Runs after the
    /// edit and the history are committed, so a throwing subscriber leaves the edit in place (R25).</summary>
    private void Finish(ImmutableArray<CellEdit> edits, RecalcResult result)
    {
        LastRecalc = new RecalcStats(result.EvaluatedFormulaCount);
        var handler = CellsChanged;
        if (handler is null || (edits.Length == 0 && result.ChangedKeys.Count == 0)) return;
        var addresses = ChangedAddresses(edits, result.ChangedKeys);
        if (addresses.Length != 0) handler(this, new CellsChangedEventArgs(addresses));
    }

    /// <summary>
    /// Sets every edited cell to its new input (<paramref name="forward"/>) or its old input, and every edited name to
    /// its new or old definition, updates the edges and name targets, and runs one recalculation with all edited cells
    /// and every name whose target changed in the changed set (§6.7: only the name's dependents re-evaluate; a
    /// casing-only redefinition re-evaluates nothing). Atomic (§9.2): on an exception every cell entry, name, edge and
    /// written value is restored and the exception propagates. Each address and each name appears at most once
    /// (enforced by the <see cref="UndoStep"/> constructor).
    /// </summary>
    private RecalcResult Apply(UndoStep step, bool forward)
    {
        var edits = step.CellEdits;
        var nameEdits = step.NameEdits;
        int n = edits.Length;
        // Pure; nothing is mutated yet. A formula keeps the cell's old value until recalc writes the new one, which is
        // what RecalcResult.ChangedCells compares against (RecalcResult contract).
        var sheets = new Sheet[n];
        var olds = new Entry?[n];
        var news = new Entry?[n];
        for (int i = 0; i < n; i++)
        {
            string input = forward ? edits[i].NewInput : edits[i].OldInput;
            sheets[i] = _sheetsById.TryGetValue(edits[i].Sheet, out var sheet)
                ? sheet
                : throw new InvalidOperationException($"The sheet of {edits[i].Address} no longer exists.");
            sheets[i].Cells.TryGetValue(CellKey.Pack(edits[i].Address), out olds[i]);
            news[i] = input.Length == 0 ? null : CreateEntry(input, olds[i]?.Value ?? CellValue.Blank);
        }
        long[] changed;
        if (n == 1)
        {
            _changedOne[0] = SheetCellKey.Pack(edits[0].Sheet, edits[0].Address);
            changed = _changedOne;
        }
        else
        {
            changed = new long[n];
            for (int i = 0; i < n; i++) changed[i] = SheetCellKey.Pack(edits[i].Sheet, edits[i].Address);
        }

        int m = nameEdits.Length;
        NameEntry?[] oldNames = m == 0 ? [] : new NameEntry?[m];
        NameEntry?[] newNames = m == 0 ? [] : new NameEntry?[m];
        List<string>? changedNames = null;
        for (int i = 0; i < m; i++)
        {
            var definition = forward ? nameEdits[i].After : nameEdits[i].Before;
            _names.TryGetValue(nameEdits[i].Name, out oldNames[i]);
            newNames[i] = definition is { } d ? CreateNameEntry(d) : null;
            if (!string.Equals(oldNames[i]?.RefersTo, newNames[i]?.RefersTo, StringComparison.Ordinal)
                || oldNames[i]?.Definition.Sheet != newNames[i]?.Definition.Sheet) // R77: a binding change (by stable id) is a target change
                (changedNames ??= []).Add(nameEdits[i].Name);
        }

        int touched = 0;      // cells [0, touched) may have been mutated
        int touchedNames = 0; // names [0, touchedNames) may have been mutated
        _rollback.Clear();
        _logWrites = true;
        try
        {
            for (int i = 0; i < n; i++)
            {
                touched = i + 1;
                SetEntry(sheets[i], edits[i].Address, olds[i], news[i]);
            }
            for (int i = 0; i < m; i++)
            {
                touchedNames = i + 1;
                SetNameEntry(nameEdits[i].Name, newNames[i]);
            }
            return _graph.Recalculate(changed, changedNames, null, _target, _functions);
        }
        catch
        {
            for (int i = _rollback.Count - 1; i >= 0; i--) _rollback[i].Entry.Value = _rollback[i].OldValue;
            for (int i = touchedNames - 1; i >= 0; i--) SetNameEntry(nameEdits[i].Name, oldNames[i]);
            for (int i = touched - 1; i >= 0; i--) SetEntry(sheets[i], edits[i].Address, news[i], olds[i]);
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
    private void SetEntry(Sheet sheet, CellAddress cell, Entry? from, Entry? to)
    {
        if (to is null) sheet.Cells.Remove(CellKey.Pack(cell));
        else sheet.Cells[CellKey.Pack(cell)] = to;
        if (from?.Formula is not null || to?.Formula is not null) _graph.SetFormula(sheet.Id, cell, to?.Formula);
    }
}
