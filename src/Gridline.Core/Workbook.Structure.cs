using System.Collections.Immutable;
using Gridline.Core.Addressing;
using Gridline.Core.Commands;
using Gridline.Core.Dependencies;
using Gridline.Core.Evaluation;
using Gridline.Core.Parsing;

namespace Gridline.Core;

// Insert and delete rows and columns (§9.5): validation, the reference adjustment of every formula and name
// (Commands.StructureRewriter + FormulaPrinter, §5.5), and the whole-sheet restructure undo step (§9.3, D-34, D-43).
// A structure edit builds the moved cell store and a fresh dependency graph aside, swaps them in, and re-evaluates every
// formula (R63); a failure swaps the previous objects back (§9.2).
public sealed partial class Workbook
{
    /// <summary>
    /// Inserts <paramref name="count"/> empty rows at <paramref name="row"/> (§9.5): cells at rows ≥ row move down, and
    /// every reference in every formula and name definition is adjusted (relative and absolute alike). Formulas with a
    /// changed reference are re-emitted canonically (§5.5); all others keep their text verbatim. One undo step (§9.3);
    /// an edit that moves no input and changes no formula or name is a no-op with no step and no event (R63).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">row is not 1..<see cref="MaxRows"/>, or count &lt; 1 (R62).</exception>
    /// <exception cref="InvalidOperationException">A cell with input would move past <see cref="MaxRows"/>, or a
    /// rewritten formula would exceed <see cref="MaxInputLength"/>. Nothing changes.</exception>
    public void InsertRows(int row, int count) => Restructure(StructureAxis.Rows, insert: true, row, count, nameof(row));

    /// <summary>
    /// Deletes rows [row, row+count-1] (§9.5): their cells are removed, cells below move up, and every reference is
    /// adjusted (a reference into the deleted rows becomes #REF!; a range keeps its surviving rows). Otherwise as
    /// <see cref="InsertRows"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">row &lt; 1, count &lt; 1, or row + count - 1 &gt; <see cref="MaxRows"/> (R62).</exception>
    /// <exception cref="InvalidOperationException">A rewritten formula would exceed <see cref="MaxInputLength"/>. Nothing changes.</exception>
    public void DeleteRows(int row, int count) => Restructure(StructureAxis.Rows, insert: false, row, count, nameof(row));

    /// <summary>Inserts <paramref name="count"/> empty columns at the 1-based <paramref name="column"/>; the column
    /// counterpart of <see cref="InsertRows"/> (§9.5).</summary>
    /// <exception cref="ArgumentOutOfRangeException">column is not 1..<see cref="MaxColumns"/>, or count &lt; 1 (R62).</exception>
    /// <exception cref="InvalidOperationException">A cell with input would move past <see cref="MaxColumns"/>, or a
    /// rewritten formula would exceed <see cref="MaxInputLength"/>. Nothing changes.</exception>
    public void InsertColumns(int column, int count) => Restructure(StructureAxis.Columns, insert: true, column, count, nameof(column));

    /// <summary>Deletes columns [column, column+count-1] (1-based); the column counterpart of <see cref="DeleteRows"/> (§9.5).</summary>
    /// <exception cref="ArgumentOutOfRangeException">column &lt; 1, count &lt; 1, or column + count - 1 &gt; <see cref="MaxColumns"/> (R62).</exception>
    /// <exception cref="InvalidOperationException">A rewritten formula would exceed <see cref="MaxInputLength"/>. Nothing changes.</exception>
    public void DeleteColumns(int column, int count) => Restructure(StructureAxis.Columns, insert: false, column, count, nameof(column));

    /// <summary>Validates, computes the step payload (pure), and performs it unless it is a no-op (R63). The edit acts on
    /// the active sheet (ruling P5-2, R84): its cells move, and so do the unqualified references of its formulas, the
    /// qualified references to it from every sheet, and the names pointing into it (R77, R84). References to other sheets are untouched.</summary>
    private void Restructure(StructureAxis axis, bool insert, int start, int count, string startParam)
    {
        bool rows = axis == StructureAxis.Rows;
        int bound = rows ? MaxRows : MaxColumns;
        string unit = rows ? "row" : "column";
        if (start < 1 || start > bound)
            throw new ArgumentOutOfRangeException(startParam, start, $"The {unit} must be 1..{bound}.");
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), count, "The count must be at least 1.");
        if (!insert && (long)start + count - 1 > bound)
            throw new ArgumentOutOfRangeException(nameof(count), count, $"The deleted {unit}s {start}..{(long)start + count - 1} exceed {bound}.");

        var edited = _active;
        var map = new StructureRewriter(axis, insert, start, count, edited.Name);
        bool moves = false;
        var removed = ImmutableArray.CreateBuilder<CellEdit>();
        var rewrites = ImmutableArray.CreateBuilder<CellEdit>();
        var walk = new Stack<FormulaNode>();
        map.FormulaOnEditedSheet = true;
        foreach (var (key, entry) in edited.Cells)
        {
            var cell = CellKey.Unpack(key);
            int to = map.MapPosition(map.PositionOf(cell));
            if (to < 0)
            {
                if (insert)
                    throw new InvalidOperationException(
                        $"Inserting {count} {unit}(s) at {start} would move {cell} past {unit} {bound}; nothing was changed.");
                removed.Add(new CellEdit(cell, entry.Input, "") { Sheet = edited.Id });
                continue;
            }
            if (to != map.PositionOf(cell)) moves = true;
            if (entry.Formula?.Root is not { } root) continue; // constants and #PARSE! formulas stay verbatim (R39)
            if (!map.Affects(root, walk)) continue; // §9.5: no changed reference, text kept verbatim (and not printed)
            map.Reset();
            string printed = FormulaPrinter.Print(root, map, _nameCasing, _sheetCasing);
            if (!map.Changed) continue; // §9.5: no changed reference, text kept verbatim
            var at = map.With(cell, to);
            if (printed.Length > MaxInputLength)
                throw new InvalidOperationException(
                    $"The formula moved to {at} would exceed {MaxInputLength} UTF-16 code units once rewritten; nothing was changed.");
            rewrites.Add(new CellEdit(at, entry.Input, printed) { Sheet = edited.Id });
        }

        // R84: qualified references to the edited sheet from the other sheets; their cells stay where they are.
        map.FormulaOnEditedSheet = false;
        foreach (var other in _sheets)
        {
            if (other == edited) continue;
            foreach (var (key, entry) in other.Cells)
            {
                if (entry.Formula?.Root is not { } root || !map.Affects(root, walk)) continue;
                map.Reset();
                string printed = FormulaPrinter.Print(root, map, _nameCasing, _sheetCasing);
                if (!map.Changed) continue;
                var at = CellKey.Unpack(key);
                if (printed.Length > MaxInputLength)
                    throw new InvalidOperationException(
                        $"The formula at {other.Prefix}{at} would exceed {MaxInputLength} UTF-16 code units once rewritten; nothing was changed.");
                rewrites.Add(new CellEdit(at, entry.Input, printed) { Sheet = other.Id });
            }
        }

        var nameEdits = ImmutableArray.CreateBuilder<NameEdit>();
        foreach (var entry in _names.Values)
        {
            if (entry.Definition.Sheet != edited.Id) continue; // R84: a name pointing into another sheet is untouched
            string adjusted = AdjustRefersTo(entry.Target, map, edited.Prefix);
            if (!string.Equals(adjusted, entry.RefersTo, StringComparison.Ordinal))
                nameEdits.Add(new NameEdit(entry.Definition, entry.Definition with { RefersTo = adjusted }));
        }

        if (!moves && removed.Count == 0 && rewrites.Count == 0 && nameEdits.Count == 0)
        {
            LastRecalc = default; // R63: no step, no event
            return;
        }
        var step = new UndoStep(
            new StructureEdit(axis, insert, start, count, rewrites.DrainToImmutable(), removed.DrainToImmutable()) { Sheet = edited.Id },
            nameEdits.DrainToImmutable());
        var (result, changed) = ApplyStructure(step, forward: true);
        _history.Record(step);
        FinishStructure(result, changed);
    }

    /// <summary>The §9.5 adjustment of a name target on the edited sheet, as a canonical qualified refersTo
    /// (<paramref name="prefix"/> + cells, R58, R77); <c>#REF!</c> once nothing of it survives (§9.6). A deleted target
    /// stays deleted.</summary>
    private static string AdjustRefersTo(NameTarget target, StructureRewriter map, string prefix)
    {
        if (target.Kind == NameTargetKind.Invalid) return NameSyntax.DeletedTarget;
        var tl = target.Range.TopLeft;
        var br = target.Range.BottomRight;
        if (target.Kind == NameTargetKind.Cell)
        {
            int to = map.MapPosition(map.PositionOf(tl));
            return to < 0 ? NameSyntax.DeletedTarget : prefix + Absolute(map.With(tl, to));
        }
        var span = map.MapSpan(map.PositionOf(tl), map.PositionOf(br));
        if (span is not { } s) return NameSyntax.DeletedTarget;
        return prefix + Absolute(map.With(tl, s.Lo)) + ":" + Absolute(map.With(br, s.Hi));
    }

    private static string Absolute(CellAddress a) => new CellReference(a, ColumnAbsolute: true, RowAbsolute: true).ToString();

    /// <summary>
    /// Applies a structure step forward (redo, or the first application) or backward (undo). The new cell stores, name
    /// entries and dependency graph are built aside first (pure); then they are swapped in and every formula is
    /// re-evaluated (R63). On the edited sheet, moved cells keep their entry objects (and parsed formulas: references
    /// are positions); rewritten and restored cells get new entries. Another sheet keeps its store unless one of its
    /// formulas was rewritten (a qualified reference to the edited sheet, R84); then the store is copied with those
    /// entries replaced in place. Returns the recalc result and the R64 changed addresses: every address whose input or
    /// value differs, comparing the same address before and after (formatted per R81). Atomic (§9.2): on an exception
    /// the previous stores, graph, names and every written value are restored.
    /// </summary>
    private (RecalcResult Result, string[] Changed) ApplyStructure(UndoStep step, bool forward)
    {
        var s = step.Structure ?? throw new InvalidOperationException("Not a structure step.");
        var edited = _sheetsById.TryGetValue(s.Sheet, out var found)
            ? found
            : throw new InvalidOperationException("The edited sheet no longer exists.");
        var map = new StructureRewriter(s.Axis, s.IsInsert, s.Start, s.Count);

        // The rewritten texts, keyed by their position after the edit (forward: the new text; backward: the old one).
        var texts = new Dictionary<long, string>(s.Rewrites.Length);
        var rewrittenSheets = new HashSet<int>();
        foreach (var edit in s.Rewrites)
        {
            texts.Add(SheetCellKey.Pack(edit.Sheet, edit.Address), forward ? edit.NewInput : edit.OldInput);
            rewrittenSheets.Add(edit.Sheet);
        }

        int sheetCount = _sheets.Count;
        var oldStores = new Dictionary<int, Entry>[sheetCount];
        var newStores = new Dictionary<int, Entry>[sheetCount];
        for (int i = 0; i < sheetCount; i++)
        {
            var sheet = _sheets[i];
            var oldCells = oldStores[i] = sheet.Cells;
            if (sheet == edited)
            {
                var newCells = new Dictionary<int, Entry>(oldCells.Count + (forward ? 0 : s.Removed.Length));
                foreach (var (key, entry) in oldCells)
                {
                    var cell = CellKey.Unpack(key);
                    int pos = map.PositionOf(cell);
                    int afterKey, newKey;
                    if (forward)
                    {
                        int to = map.MapPosition(pos);
                        if (to < 0) continue; // a deleted cell (recorded in Removed); an insert never pushes input out (validated)
                        newKey = afterKey = CellKey.Pack(map.With(cell, to));
                    }
                    else
                    {
                        afterKey = key;
                        newKey = CellKey.Pack(map.With(cell, map.UnmapPosition(pos)));
                    }
                    newCells.Add(newKey, texts.TryGetValue(SheetCellKey.Pack(sheet.Id, afterKey), out string? text)
                        ? CreateEntry(text, entry.Value)
                        : entry);
                }
                if (!forward)
                    foreach (var edit in s.Removed) newCells.Add(CellKey.Pack(edit.Address), CreateEntry(edit.OldInput, CellValue.Blank));
                newStores[i] = newCells;
            }
            else if (rewrittenSheets.Contains(sheet.Id))
            {
                var newCells = new Dictionary<int, Entry>(oldCells);
                foreach (var edit in s.Rewrites)
                {
                    if (edit.Sheet != sheet.Id) continue;
                    int key = CellKey.Pack(edit.Address);
                    newCells[key] = CreateEntry(forward ? edit.NewInput : edit.OldInput, oldCells[key].Value);
                }
                newStores[i] = newCells;
            }
            else newStores[i] = oldCells;
        }

        var nameEdits = step.NameEdits;
        var oldNames = new NameEntry[nameEdits.Length];
        var newNames = new Dictionary<string, NameEntry>(nameEdits.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < nameEdits.Length; i++)
        {
            var definition = (forward ? nameEdits[i].After : nameEdits[i].Before)
                ?? throw new InvalidOperationException("A structure edit never defines or deletes a name.");
            oldNames[i] = _names[nameEdits[i].Name];
            newNames.Add(definition.Name, CreateNameEntry(definition));
        }

        var graph = new DependencyGraph();
        foreach (var sheet in _sheets) graph.SetSheet(sheet.Name, sheet.Id);
        for (int i = 0; i < sheetCount; i++)
            foreach (var (key, entry) in newStores[i])
                if (entry.Formula is not null) graph.SetFormula(_sheets[i].Id, CellKey.Unpack(key), entry.Formula);
        foreach (var current in _names.Values)
        {
            var e = newNames.TryGetValue(current.Name, out var replaced) ? replaced : current;
            if (e.Target.Kind != NameTargetKind.Invalid) graph.SetNameTarget(e.Name, e.Definition.Sheet, e.Target.Range);
        }

        var before = new CellValue[sheetCount][]; // values before, in each old store's enumeration order (R64)
        for (int i = 0; i < sheetCount; i++)
        {
            var values = before[i] = new CellValue[oldStores[i].Count];
            int b = 0;
            foreach (var entry in oldStores[i].Values) values[b++] = entry.Value;
        }

        var oldGraph = _graph;
        _rollback.Clear();
        _logWrites = true;
        try
        {
            for (int i = 0; i < sheetCount; i++) _sheets[i].Cells = newStores[i];
            _graph = graph;
            foreach (var e in newNames.Values) ReplaceNameEntry(e);
            var result = graph.RecalculateAll(_target, _functions);

            var changedKeys = new List<long>();
            for (int si = 0; si < sheetCount; si++)
            {
                int id = _sheets[si].Id;
                var oldCells = oldStores[si];
                var newCells = newStores[si];
                int i = 0;
                foreach (var (key, old) in oldCells)
                {
                    var prior = before[si][i++];
                    if (!newCells.TryGetValue(key, out var now) || !string.Equals(now.Input, old.Input, StringComparison.Ordinal)
                        || now.Value != prior)
                        changedKeys.Add(SheetCellKey.Pack(id, key));
                }
                if (!ReferenceEquals(oldCells, newCells))
                    foreach (int key in newCells.Keys)
                        if (!oldCells.ContainsKey(key)) changedKeys.Add(SheetCellKey.Pack(id, key));
            }
            return (result, ToSortedAddresses([.. changedKeys]));
        }
        catch
        {
            for (int i = _rollback.Count - 1; i >= 0; i--) _rollback[i].Entry.Value = _rollback[i].OldValue;
            foreach (var e in oldNames) ReplaceNameEntry(e);
            for (int i = 0; i < sheetCount; i++) _sheets[i].Cells = oldStores[i];
            _graph = oldGraph;
            throw;
        }
        finally
        {
            _logWrites = false;
            if (_rollback.Capacity > RollbackRetainCapacity) _rollback = [];
            else _rollback.Clear();
        }
    }

    /// <summary>Replaces an existing name's entry in the store only; the caller owns the graph's name targets.</summary>
    private void ReplaceNameEntry(NameEntry entry)
    {
        _names.Remove(entry.Name);
        _names.Add(entry.Name, entry);
    }

    /// <summary>Sets LastRecalc (every formula evaluated, R63) and raises CellsChanged once with the R64 addresses,
    /// unless none changed (§9.2). Runs after the history is committed (R25).</summary>
    private void FinishStructure(RecalcResult result, string[] changed)
    {
        LastRecalc = new RecalcStats(result.EvaluatedFormulaCount);
        var handler = CellsChanged;
        if (handler is null || changed.Length == 0) return;
        handler(this, new CellsChangedEventArgs(changed));
    }
}
