using System.Collections.Immutable;
using Gridline.Core.Addressing;
using Gridline.Core.Commands;
using Gridline.Core.Dependencies;
using Gridline.Core.Parsing;

namespace Gridline.Core;

// RenameSheet, DeleteSheet and MoveSheet (CR1.3, R78-R81, R85, D-54): validation, the formula and name rewrite (only the
// formulas whose text names the sheet, found through the dependency graph's sheet-name index, P-8), and their undo step
// (SheetChange). Each change is applied in place and is atomic (§9.2): a failure part-way restores every cell, name,
// binding, sheet-list entry, active sheet and written value.
public sealed partial class Workbook
{
    /// <summary>
    /// Renames the sheet <paramref name="oldName"/> (matched case-insensitively) to <paramref name="newName"/> (CR1.3); one
    /// undo step (§9.3). Every formula and name that refers to the old name is rewritten to the new one with canonical
    /// quoting and re-emitted (§5.5, R85); all other formulas keep their text verbatim, #PARSE! inputs included (R39). A
    /// formula that referred to a missing sheet named <paramref name="newName"/> now resolves, its text unchanged. A
    /// casing-only rename is a change; renaming to the ordinal-identical name is a no-op with no step and no event (R79).
    /// The active sheet stays active under its new name (R80). <see cref="CellsChanged"/> reports the rewritten formulas
    /// and every cell whose value changed (R81).
    /// </summary>
    /// <exception cref="ArgumentNullException">A name is null.</exception>
    /// <exception cref="ArgumentException">No sheet is named <paramref name="oldName"/>, <paramref name="newName"/> is not a
    /// valid sheet name (CR1.1), or another sheet already has that name case-insensitively (R78).</exception>
    /// <exception cref="InvalidOperationException">A rewritten formula would exceed <see cref="MaxInputLength"/>. All
    /// errors leave the workbook unchanged.</exception>
    public void RenameSheet(string oldName, string newName)
    {
        ArgumentNullException.ThrowIfNull(oldName);
        ArgumentNullException.ThrowIfNull(newName);
        var sheet = FindSheet(oldName, nameof(oldName));
        SheetName.Validate(newName, nameof(newName));
        if (_sheetsByName.TryGetValue(newName, out var other) && other != sheet)
            throw new ArgumentException($"A sheet named '{other.Name}' already exists.", nameof(newName));
        if (string.Equals(sheet.Name, newName, StringComparison.Ordinal))
        {
            LastRecalc = default; // R79: no step, no event
            return;
        }

        var rewrites = RewriteReferencing(sheet, newName);
        string newPrefix = SheetName.Format(newName) + "!";
        var nameEdits = ImmutableArray.CreateBuilder<NameEdit>();
        foreach (var entry in _names.Values)
        {
            if (entry.Definition.Sheet != sheet.Id || IsDeleted(entry.Definition)) continue;
            // The stored text is the old prefix + the cell part (R77); the sheet id stays (D-53).
            if (!SheetName.TrySplitQualified(entry.RefersTo, out _, out string cells))
                throw new InvalidOperationException($"Stored refersTo '{entry.RefersTo}' is not qualified.");
            nameEdits.Add(new NameEdit(entry.Definition, entry.Definition with { RefersTo = newPrefix + cells }));
        }

        int index = _sheets.IndexOf(sheet);
        var step = new UndoStep(new SheetChange(SheetChangeKind.Rename, sheet.Id, sheet.Name, newName, index, index),
            rewrites, nameEdits.DrainToImmutable());
        var (result, changed) = ApplySheetChange(step, forward: true);
        _history.Record(step);
        FinishSheetChange(result, changed);
    }

    /// <summary>
    /// Deletes the sheet <paramref name="name"/> (matched case-insensitively) and its cells (CR1.3); one undo step (§9.3).
    /// Every reference, single or range, whose prefix names it becomes <c>#REF!</c> and its formula is re-emitted; names
    /// pointing into it get refersTo <c>#REF!</c>; references to other missing sheets are untouched (R85). When it was
    /// the active sheet, the sheet now at its index becomes active, or the last sheet (R80). Undo restores the sheet at
    /// its index with its cells, the names, and the verbatim text of every rewritten formula.
    /// </summary>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="ArgumentException">No sheet has that name (R78).</exception>
    /// <exception cref="InvalidOperationException">It is the only sheet, or a rewritten formula would exceed
    /// <see cref="MaxInputLength"/>. All errors leave the workbook unchanged.</exception>
    public void DeleteSheet(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sheet = FindSheet(name, nameof(name));
        if (_sheets.Count == 1)
            throw new InvalidOperationException("A workbook keeps at least one sheet; the last sheet cannot be deleted.");

        var rewrites = RewriteReferencing(sheet, null);
        var removed = new CellEdit[sheet.Cells.Count];
        int n = 0;
        foreach (var (key, entry) in sheet.Cells)
            removed[n++] = new CellEdit(CellKey.Unpack(key), entry.Input, "") { Sheet = sheet.Id };
        Array.Sort(removed, (a, b) => CellKey.Pack(a.Address).CompareTo(CellKey.Pack(b.Address)));
        var nameEdits = ImmutableArray.CreateBuilder<NameEdit>();
        foreach (var entry in _names.Values)
            if (entry.Definition.Sheet == sheet.Id && !IsDeleted(entry.Definition))
                nameEdits.Add(new NameEdit(entry.Definition, entry.Definition with { RefersTo = NameSyntax.DeletedTarget }));

        int index = _sheets.IndexOf(sheet);
        var change = new SheetChange(SheetChangeKind.Delete, sheet.Id, sheet.Name, sheet.Name, index, index)
        {
            Removed = ImmutableArray.Create(removed),
        };
        var step = new UndoStep(change, rewrites, nameEdits.DrainToImmutable());
        var (result, changed) = ApplySheetChange(step, forward: true);
        _history.Record(step);
        FinishSheetChange(result, changed);
    }

    /// <summary>
    /// Moves the sheet <paramref name="name"/> (matched case-insensitively) to <paramref name="newIndex"/> in the order
    /// (CR1.3); one undo step (§9.3). Only the order changes: no formula, name, value or active sheet;
    /// <see cref="GetUsedAddresses"/> follows the new order. Moving to the current index is a no-op with no step (R79).
    /// No <see cref="CellsChanged"/> is raised (R81).
    /// </summary>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="ArgumentException">No sheet has that name.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="newIndex"/> is outside 0..<see cref="SheetNames"/>.Count - 1.
    /// All errors leave the workbook unchanged (R78).</exception>
    public void MoveSheet(string name, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sheet = FindSheet(name, nameof(name));
        if (newIndex < 0 || newIndex >= _sheets.Count)
            throw new ArgumentOutOfRangeException(nameof(newIndex), newIndex, $"The index must be 0..{_sheets.Count - 1}.");
        int index = _sheets.IndexOf(sheet);
        if (index == newIndex)
        {
            LastRecalc = default; // R79: no step, no event
            return;
        }
        var step = new UndoStep(new SheetChange(SheetChangeKind.Move, sheet.Id, sheet.Name, sheet.Name, index, newIndex), [], []);
        var (result, changed) = ApplySheetChange(step, forward: true);
        _history.Record(step);
        FinishSheetChange(result, changed);
    }

    private static bool IsDeleted(NameDefinition definition) =>
        string.Equals(definition.RefersTo, NameSyntax.DeletedTarget, StringComparison.Ordinal);

    /// <summary>
    /// The rewrites of a rename to <paramref name="newName"/> (or a deletion when null) of <paramref name="sheet"/>: every
    /// formula whose text names the sheet (the graph's sheet-name index, so O(referencing formulas), P-8), printed with the
    /// prefix replaced (or <c>#REF!</c>) per §5.5. Formulas on a deleted sheet are skipped (they go with it); a formula whose
    /// printed text equals its input is kept. Pure.
    /// </summary>
    /// <exception cref="InvalidOperationException">A rewritten formula would exceed <see cref="MaxInputLength"/> (R85, D-43).</exception>
    private ImmutableArray<CellEdit> RewriteReferencing(Sheet sheet, string? newName)
    {
        long[] users = _graph.GetSheetNameDependents(sheet.Name);
        Array.Sort(users); // deterministic order (and error message)
        var rewriter = new SheetRewriter(sheet.Name, newName);
        ISheetCasingResolver casing = newName is null ? _sheetCasing : new RenamedSheetCasing(this, newName);
        var rewrites = ImmutableArray.CreateBuilder<CellEdit>(users.Length);
        foreach (long key in users)
        {
            int id = SheetCellKey.Sheet(key);
            if (newName is null && id == sheet.Id) continue; // deleted with its sheet
            var on = _sheetsById[id];
            var entry = on.Cells[SheetCellKey.Cell(key)];
            if (entry.Formula?.Root is not { } root) continue; // #PARSE! has no precedents; kept verbatim (R39)
            string printed = FormulaPrinter.Print(root, rewriter, _nameCasing, casing);
            if (string.Equals(printed, entry.Input, StringComparison.Ordinal)) continue;
            var at = SheetCellKey.Address(key);
            if (printed.Length > MaxInputLength)
                throw new InvalidOperationException(
                    $"The formula at {on.Prefix}{at} would exceed {MaxInputLength} UTF-16 code units once rewritten; nothing was changed.");
            rewrites.Add(new CellEdit(at, entry.Input, printed) { Sheet = id });
        }
        return rewrites.Count == rewrites.Capacity ? rewrites.MoveToImmutable() : rewrites.ToImmutable();
    }

    /// <summary>Applies a rename, delete or move step forward (do, redo) or backward (undo). Returns the recalc result and
    /// the changed cells as <see cref="SheetCellKey"/> keys (formatted per R81 only when raised).</summary>
    private (RecalcResult Result, long[] Changed) ApplySheetChange(UndoStep step, bool forward)
    {
        var change = step.SheetChange ?? throw new InvalidOperationException("Not a sheet change step.");
        return change.Kind switch
        {
            SheetChangeKind.Rename => ApplyRename(step, change, forward),
            SheetChangeKind.Delete => forward ? ApplyDelete(step, change) : ApplyRestore(step, change),
            _ => ApplyMove(change, forward),
        };
    }

    private (RecalcResult, long[]) ApplyMove(SheetChange change, bool forward)
    {
        var sheet = ExistingSheet(change.Id);
        int from = forward ? change.OldIndex : change.NewIndex, to = forward ? change.NewIndex : change.OldIndex;
        if (from < 0 || from >= _sheets.Count || _sheets[from] != sheet || to < 0 || to >= _sheets.Count)
            throw new InvalidOperationException("The sheet is not where the step expects it.");
        _sheets.RemoveAt(from);
        _sheets.Insert(to, sheet); // cannot fail: 0 <= to < Count before the removal
        return (RecalcResult.None, []);
    }

    /// <summary>
    /// Rename (either direction): the rewritten formulas get their recorded text, the sheet its new name and binding, the
    /// names their new prefix. The rewritten formulas reference the same cells as before, so their values cannot change:
    /// they keep their edges (<see cref="DependencyGraph.RenameSheetReferences"/>) and are not re-evaluated. Forward, their
    /// trees are renamed rather than re-parsed (<see cref="SheetRenamer"/>); undo parses the verbatim old text. Only
    /// formulas whose resolution changes are recalculated: those that named the new name as a missing sheet (now bound),
    /// and those still naming the old name afterwards (now missing). A rename that changes no value recalculates nothing (P-8).
    /// </summary>
    private (RecalcResult, long[]) ApplyRename(UndoStep step, SheetChange change, bool forward)
    {
        var sheet = ExistingSheet(change.Id);
        string from = forward ? change.OldName : change.NewName, to = forward ? change.NewName : change.OldName;
        if (!string.Equals(sheet.Name, from, StringComparison.Ordinal))
            throw new InvalidOperationException("The sheet does not have the name the step expects.");
        bool rebinds = !string.Equals(from, to, StringComparison.OrdinalIgnoreCase);

        // Pure: every rewritten cell is a formula before and after.
        var edits = step.CellEdits;
        var keys = new int[edits.Length];
        var sheets = new Sheet[edits.Length];
        var olds = new Entry[edits.Length];
        var news = new Entry[edits.Length];
        var rewrites = new (long Key, ParsedFormula Formula)[edits.Length];
        for (int i = 0; i < edits.Length; i++)
        {
            sheets[i] = ExistingSheet(edits[i].Sheet);
            keys[i] = CellKey.Pack(edits[i].Address);
            if (!sheets[i].Cells.TryGetValue(keys[i], out var old) || old.Formula?.Root is not { } root)
                throw new InvalidOperationException($"The rewritten cell {edits[i].Address} is not a formula.");
            olds[i] = old;
            news[i] = forward
                ? new Entry(edits[i].NewInput, ParsedFormula.Ok(SheetRenamer.Rename(root, from, to)), old.Value)
                : CreateEntry(edits[i].OldInput, old.Value);
            rewrites[i] = (SheetCellKey.Pack(edits[i].Sheet, keys[i]), news[i].Formula
                ?? throw new InvalidOperationException($"The rewritten cell {edits[i].Address} would not be a formula."));
        }
        var names = PrepareNames(step.NameEdits, forward);
        var seeds = new List<long>();
        if (rebinds) seeds.AddRange(_graph.GetSheetNameDependents(to)); // missing-sheet references that now resolve

        int touched = 0, touchedNames = 0;
        bool rebound = false, renamed = false;
        _rollback.Clear();
        _logWrites = true;
        try
        {
            _graph.RenameSheetReferences(from, to, sheet.Id, rewrites);
            rebound = true;
            for (; touched < edits.Length; touched++) sheets[touched].Cells[keys[touched]] = news[touched];
            _sheetsByName.Remove(from);
            sheet.SetName(to);
            _sheetsByName.Add(to, sheet);
            renamed = true;
            for (; touchedNames < names.Length; touchedNames++) SetNameEntry(step.NameEdits[touchedNames].Name, names.News[touchedNames]);
            if (rebinds) seeds.AddRange(_graph.GetSheetNameDependents(from)); // now missing
            var result = seeds.Count == 0 ? RecalcResult.None : _graph.Recalculate(seeds, null, null, _target, _functions);
            return (result, Concat(CellKeys(edits), result.ChangedKeys));
        }
        catch
        {
            RollBackValues();
            for (int i = touchedNames - 1; i >= 0; i--) SetNameEntry(step.NameEdits[i].Name, names.Olds[i]);
            if (renamed)
            {
                _sheetsByName.Remove(to);
                sheet.SetName(from);
                _sheetsByName.Add(from, sheet);
            }
            for (int i = touched - 1; i >= 0; i--) sheets[i].Cells[keys[i]] = olds[i];
            if (rebound)
            {
                var back = new (long Key, ParsedFormula Formula)[edits.Length];
                for (int i = 0; i < back.Length; i++) back[i] = (rewrites[i].Key, olds[i].Formula!);
                _graph.RenameSheetReferences(to, from, sheet.Id, back);
            }
            throw;
        }
        finally
        {
            EndLogging();
        }
    }

    /// <summary>Delete forward (do, redo): rewrite the referencing formulas to #REF!, take the sheet's formulas out of the
    /// graph, set its names to #REF!, remove it (R80), and recalculate the rewritten formulas and the names' dependents.
    /// The removed sheet object keeps its cells; undo builds a fresh one from <see cref="SheetChange.Removed"/>.</summary>
    private (RecalcResult, long[]) ApplyDelete(UndoStep step, SheetChange change)
    {
        var sheet = ExistingSheet(change.Id);
        int index = _sheets.IndexOf(sheet);
        if (index != change.OldIndex || sheet.Name != change.OldName || sheet.Cells.Count != change.Removed.Length)
            throw new InvalidOperationException("The sheet is not as the step expects it.");
        var cells = PrepareCells(step.CellEdits, forward: true);
        var names = PrepareNames(step.NameEdits, forward: true);
        var removed = change.Removed;
        var activeBefore = _active;

        int touched = 0, cleared = 0, touchedNames = 0;
        bool removedSheet = false;
        _rollback.Clear();
        _logWrites = true;
        try
        {
            for (; touched < cells.Length; touched++) SetEntry(cells.Sheets[touched], step.CellEdits[touched].Address, cells.Olds[touched], cells.News[touched]);
            for (; cleared < removed.Length; cleared++)
                if (sheet.Cells[CellKey.Pack(removed[cleared].Address)].Formula is not null)
                    _graph.SetFormula(sheet.Id, removed[cleared].Address, null);
            for (; touchedNames < names.Length; touchedNames++) SetNameEntry(step.NameEdits[touchedNames].Name, names.News[touchedNames]);
            RemoveSheet(sheet);
            removedSheet = true;
            var rewritten = CellKeys(step.CellEdits);
            var result = _graph.Recalculate(rewritten, names.Changed, null, _target, _functions);
            return (result, Concat(rewritten, result.ChangedKeys));
        }
        catch
        {
            RollBackValues();
            if (removedSheet) InsertSheet(sheet, index);
            _active = activeBefore;
            for (int i = touchedNames - 1; i >= 0; i--) SetNameEntry(step.NameEdits[i].Name, names.Olds[i]);
            for (int i = cleared - 1; i >= 0; i--)
                if (sheet.Cells[CellKey.Pack(removed[i].Address)].Formula is { } formula)
                    _graph.SetFormula(sheet.Id, removed[i].Address, formula);
            for (int i = touched - 1; i >= 0; i--) SetEntry(cells.Sheets[i], step.CellEdits[i].Address, cells.News[i], cells.Olds[i]);
            throw;
        }
        finally
        {
            EndLogging();
        }
    }

    /// <summary>Delete backward (undo): re-create the sheet with its id at its index (the active sheet does not change,
    /// R80), restore its cells, the verbatim text of the rewritten formulas and the names, and recalculate the restored
    /// cells, the rewritten formulas, the names' dependents and every formula naming the sheet.</summary>
    private (RecalcResult, long[]) ApplyRestore(UndoStep step, SheetChange change)
    {
        if (_sheetsById.ContainsKey(change.Id)) throw new InvalidOperationException("The deleted sheet still exists.");
        var sheet = new Sheet(this, change.Id, change.OldName);
        var removed = change.Removed;
        var entries = new Entry[removed.Length];
        for (int i = 0; i < removed.Length; i++) entries[i] = CreateEntry(removed[i].OldInput, CellValue.Blank); // pure
        var cells = PrepareCells(step.CellEdits, forward: false);
        var names = PrepareNames(step.NameEdits, forward: false);
        var activeBefore = _active;

        int restored = 0, touched = 0, touchedNames = 0;
        bool inserted = false;
        _rollback.Clear();
        _logWrites = true;
        try
        {
            InsertSheet(sheet, change.OldIndex);
            inserted = true;
            for (; restored < removed.Length; restored++) SetEntry(sheet, removed[restored].Address, null, entries[restored]);
            for (; touched < cells.Length; touched++) SetEntry(cells.Sheets[touched], step.CellEdits[touched].Address, cells.Olds[touched], cells.News[touched]);
            for (; touchedNames < names.Length; touchedNames++) SetNameEntry(step.NameEdits[touchedNames].Name, names.News[touchedNames]);
            var seeds = Concat(CellKeys(removed), CellKeys(step.CellEdits));
            var result = _graph.Recalculate(seeds, names.Changed, [change.OldName], _target, _functions);
            return (result, Concat(seeds, result.ChangedKeys));
        }
        catch
        {
            RollBackValues();
            for (int i = touchedNames - 1; i >= 0; i--) SetNameEntry(step.NameEdits[i].Name, names.Olds[i]);
            for (int i = touched - 1; i >= 0; i--) SetEntry(cells.Sheets[i], step.CellEdits[i].Address, cells.News[i], cells.Olds[i]);
            for (int i = restored - 1; i >= 0; i--) SetEntry(sheet, removed[i].Address, entries[i], null);
            if (inserted) RemoveSheet(sheet);
            _active = activeBefore;
            throw;
        }
        finally
        {
            EndLogging();
        }
    }

    /// <summary>The prepared (pure) cell side of a step: each edit's sheet, current entry and target entry. A rewritten
    /// formula keeps its current value until a recalculation replaces it.</summary>
    private readonly record struct PreparedCells(Sheet[] Sheets, Entry?[] Olds, Entry?[] News)
    {
        public int Length => Sheets.Length;
    }

    private PreparedCells PrepareCells(ImmutableArray<CellEdit> edits, bool forward)
    {
        var sheets = new Sheet[edits.Length];
        var olds = new Entry?[edits.Length];
        var news = new Entry?[edits.Length];
        for (int i = 0; i < edits.Length; i++)
        {
            sheets[i] = ExistingSheet(edits[i].Sheet);
            sheets[i].Cells.TryGetValue(CellKey.Pack(edits[i].Address), out olds[i]);
            string input = forward ? edits[i].NewInput : edits[i].OldInput;
            news[i] = input.Length == 0 ? null : CreateEntry(input, olds[i]?.Value ?? CellValue.Blank);
        }
        return new PreparedCells(sheets, olds, news);
    }

    /// <summary>The prepared (pure) name side of a step, and the names whose target changed (delete and restore: the
    /// dependents re-evaluate; a rename keeps every target, so none).</summary>
    private readonly record struct PreparedNames(NameEntry?[] Olds, NameEntry?[] News, List<string>? Changed)
    {
        public int Length => Olds.Length;
    }

    private PreparedNames PrepareNames(ImmutableArray<NameEdit> edits, bool forward)
    {
        var olds = new NameEntry?[edits.Length];
        var news = new NameEntry?[edits.Length];
        List<string>? changed = null;
        for (int i = 0; i < edits.Length; i++)
        {
            _names.TryGetValue(edits[i].Name, out olds[i]);
            news[i] = (forward ? edits[i].After : edits[i].Before) is { } d ? CreateNameEntry(d) : null;
            if (olds[i]?.Target != news[i]?.Target || olds[i]?.Definition.Sheet != news[i]?.Definition.Sheet)
                (changed ??= []).Add(edits[i].Name);
        }
        return new PreparedNames(olds, news, changed);
    }

    private Sheet ExistingSheet(int id) =>
        _sheetsById.TryGetValue(id, out var sheet) ? sheet : throw new InvalidOperationException("The step's sheet no longer exists.");

    private static long[] CellKeys(ImmutableArray<CellEdit> edits)
    {
        var keys = new long[edits.Length];
        for (int i = 0; i < keys.Length; i++) keys[i] = SheetCellKey.Pack(edits[i].Sheet, edits[i].Address);
        return keys;
    }

    private static long[] Concat(long[] first, IReadOnlyList<long> second)
    {
        if (second.Count == 0) return first;
        var all = new long[first.Length + second.Count];
        first.CopyTo(all, 0);
        for (int i = 0; i < second.Count; i++) all[first.Length + i] = second[i];
        return all;
    }

    private void RollBackValues()
    {
        for (int i = _rollback.Count - 1; i >= 0; i--) _rollback[i].Entry.Value = _rollback[i].OldValue;
    }

    private void EndLogging()
    {
        _logWrites = false;
        if (_rollback.Capacity > RollbackRetainCapacity) _rollback = [];
        else _rollback.Clear();
    }

    /// <summary>Sets LastRecalc and raises CellsChanged once with the R81 addresses (formatted only when raised), unless
    /// none changed (§9.2, R81). Runs after the history is committed (R25).</summary>
    private void FinishSheetChange(RecalcResult result, long[] changed)
    {
        LastRecalc = new RecalcStats(result.EvaluatedFormulaCount);
        var handler = CellsChanged;
        if (handler is null || changed.Length == 0) return;
        var addresses = ToSortedAddresses(changed);
        if (addresses.Length != 0) handler(this, new CellsChangedEventArgs(addresses));
    }

    /// <summary>R82 casing while printing a rename's rewrites: the new name prints as given (a casing-only rename prints
    /// the new casing), every other sheet in its defined casing.</summary>
    private sealed class RenamedSheetCasing(Workbook owner, string newName) : ISheetCasingResolver
    {
        public string? GetDefinedCasing(string sheet) =>
            sheet.Equals(newName, StringComparison.OrdinalIgnoreCase) ? newName : owner._sheetCasing.GetDefinedCasing(sheet);
    }
}
