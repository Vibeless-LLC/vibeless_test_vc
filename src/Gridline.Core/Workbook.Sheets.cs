using System.Collections.Immutable;
using Gridline.Core.Addressing;
using Gridline.Core.Commands;
using Gridline.Core.Dependencies;
using Gridline.Core.Parsing;

namespace Gridline.Core;

// Multiple sheets (CR1.1, CR1.3, D-52): the sheet list, the active sheet, AddSheet and its undo step (SheetInsert), and
// the sheet casing resolver for canonical printing (R82). Rename, delete and move are in Workbook.SheetChanges.cs (D-54).
public sealed partial class Workbook
{
    /// <summary>The sheet names in order, each in its defined casing (CR1.3). A snapshot; later changes do not affect it.</summary>
    public IReadOnlyList<string> SheetNames
    {
        get
        {
            var names = new string[_sheets.Count];
            for (int i = 0; i < names.Length; i++) names[i] = _sheets[i].Name;
            return Array.AsReadOnly(names);
        }
    }

    /// <summary>
    /// The active sheet's name in its defined casing (CR1.3): unqualified addresses refer to it. Setting it looks the
    /// name up case-insensitively; it makes no undo step, raises no <see cref="CellsChanged"/> and leaves
    /// <see cref="IsDirty"/> unchanged (R80).
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ArgumentException">No sheet has that name. Nothing changes.</exception>
    public string ActiveSheet
    {
        get => _active.Name;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _active = FindSheet(value, nameof(value));
        }
    }

    /// <summary>The index of the active sheet in <see cref="SheetNames"/>, O(sheets). Internal: the App's sheet tab strip
    /// and per-sheet scans (CR1.8, R88, D-59).</summary>
    internal int ActiveSheetIndex => _sheets.IndexOf(_active);

    /// <summary>The number of non-empty cells on the active sheet, O(1). Internal: the App's selection-scan strategy
    /// (SelectionSum, Delete, Copy) works on the active sheet only (CR1.8, R88, D-59).</summary>
    internal int ActiveSheetUsedCellCount => _active.Cells.Count;

    /// <summary>
    /// Adds an empty sheet named <paramref name="name"/> at <paramref name="index"/>, or at the end when null (CR1.3);
    /// one undo step (§9.3). The active sheet does not change (R80). Formulas that referenced the name as a missing
    /// sheet (#REF!) recalculate, and <see cref="CellsChanged"/> reports those whose value changed (R81, R82).
    /// Undo removes the sheet; redo adds it again at the same index.
    /// </summary>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="ArgumentException">The name is not a valid sheet name (CR1.1) or equals an existing sheet's name
    /// case-insensitively.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside 0..<see cref="SheetNames"/>.Count.</exception>
    /// <exception cref="InvalidOperationException">The workbook already has 255 sheets. All errors leave it unchanged (R78).</exception>
    public void AddSheet(string name, int? index = null) => AddSheetCore(name, index, []);

    /// <summary>
    /// Internal entry (ruling P1; CSV <c>ImportInto</c>, CR1.6, R76): as <see cref="AddSheet"/> with the sheet appended and
    /// already holding <paramref name="cells"/> (unqualified address, input; empty inputs are skipped). Everything is
    /// validated before anything changes; then the sheet and its cells are added by the one sheet-insert step, with one
    /// recalculation and one undo step. Undo removes the sheet; redo adds it again with its cells.
    /// </summary>
    /// <exception cref="ArgumentException">As <see cref="AddSheet"/>, or an address is invalid or qualified, an input is
    /// null or too long, or an address appears twice (compared canonically).</exception>
    /// <exception cref="InvalidOperationException">The workbook already has 255 sheets.</exception>
    internal void AddSheetWithCells(string name, IEnumerable<KeyValuePair<string, string>> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        AddSheetCore(name, null, cells);
    }

    private void AddSheetCore(string name, int? index, IEnumerable<KeyValuePair<string, string>> cells)
    {
        ArgumentNullException.ThrowIfNull(name);
        SheetName.Validate(name, nameof(name));
        if (_sheetsByName.TryGetValue(name, out var existing))
            throw new ArgumentException($"A sheet named '{existing.Name}' already exists.", nameof(name));
        if (_sheets.Count >= MaxSheetCount)
            throw new InvalidOperationException($"A workbook has at most {MaxSheetCount} sheets; nothing was changed.");
        int at = index ?? _sheets.Count;
        if (at < 0 || at > _sheets.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The index must be 0..{_sheets.Count}.");

        var edits = ImmutableArray.CreateBuilder<CellEdit>();
        var seen = new HashSet<int>();
        foreach (var (address, input) in cells)
        {
            int key = CellKey.Pack(ParseAddress(address, nameof(cells)));
            ValidateInput(input, nameof(cells));
            if (!seen.Add(key)) throw new ArgumentException($"Duplicate cell address '{address}'.", nameof(cells));
            if (input.Length != 0) edits.Add(new CellEdit(CellKey.Unpack(key), "", input) { Sheet = _nextSheetId });
        }

        var step = new UndoStep(new SheetInsert(_nextSheetId, name, at), edits.ToImmutable());
        var (result, changed) = ApplySheetInsert(step, forward: true);
        _nextSheetId++; // only once the sheet exists, so a failed call allocates no id
        _history.Record(step);
        FinishSheetStep(result, changed);
    }

    /// <summary>The sheet named <paramref name="name"/>, case-insensitively (R78).</summary>
    /// <exception cref="ArgumentException">No such sheet.</exception>
    private Sheet FindSheet(string name, string paramName) =>
        _sheetsByName.TryGetValue(name, out var sheet)
            ? sheet
            : throw new ArgumentException($"Unknown sheet '{Clip(name)}'.", paramName);

    /// <summary>Puts <paramref name="sheet"/> at <paramref name="index"/> in the order, registers it and binds its name in
    /// the dependency graph (formulas that name it re-resolve their edges; the caller recalculates them).</summary>
    /// <remarks>Atomic: the index is checked first, the lookups and the binding are added before the list insert (which
    /// cannot then fail), and a failure part-way removes what was added (D-54).</remarks>
    private void InsertSheet(Sheet sheet, int index)
    {
        if (index < 0 || index > _sheets.Count)
            throw new InvalidOperationException($"Sheet index {index} is outside 0..{_sheets.Count}.");
        _sheetsById.Add(sheet.Id, sheet);
        try
        {
            _sheetsByName.Add(sheet.Name, sheet);
            try
            {
                _graph.SetSheet(sheet.Name, sheet.Id);
            }
            catch
            {
                _sheetsByName.Remove(sheet.Name);
                throw;
            }
        }
        catch
        {
            _sheetsById.Remove(sheet.Id);
            throw;
        }
        _sheets.Insert(index, sheet);
    }

    /// <summary>Reverses <see cref="InsertSheet"/>. The sheet's formulas must already be out of the graph. When the
    /// active sheet is removed, the sheet now at its index becomes active, or the last sheet (R80). The last sheet is
    /// never removed (R78).</summary>
    private void RemoveSheet(Sheet sheet)
    {
        int index = _sheets.IndexOf(sheet);
        if (index < 0 || _sheets.Count == 1) throw new InvalidOperationException("The sheet cannot be removed.");
        _graph.SetSheet(sheet.Name, null);
        _sheets.RemoveAt(index);
        _sheetsById.Remove(sheet.Id);
        _sheetsByName.Remove(sheet.Name);
        if (_active == sheet) _active = _sheets[Math.Min(index, _sheets.Count - 1)];
    }

    /// <summary>
    /// Applies a sheet-insert step (D-52) forward (do, redo: insert the sheet with its recorded id at its index, then its
    /// cells) or backward (undo: clear its cells, then remove it, R80), and runs one recalculation seeded with the sheet's
    /// cells and with every formula that names the sheet (R82). Returns the result and the R81 changed addresses: the
    /// sheet's non-empty cells when created, plus every formula whose value changed (a removed sheet's cells are not
    /// reported). Atomic (§9.2): on an exception the sheet list, cells, graph and written values are restored.
    /// </summary>
    private (RecalcResult Result, string[] Changed) ApplySheetInsert(UndoStep step, bool forward)
    {
        var insert = step.SheetInsert ?? throw new InvalidOperationException("Not a sheet step.");
        var edits = step.CellEdits;
        var activeBefore = _active;
        Sheet sheet;
        Entry?[] entries = new Entry?[edits.Length];
        if (forward)
        {
            sheet = new Sheet(this, insert.Id, insert.Name);
            for (int i = 0; i < edits.Length; i++)
                entries[i] = edits[i].NewInput.Length == 0 ? null : CreateEntry(edits[i].NewInput, CellValue.Blank); // pure
        }
        else
        {
            sheet = _sheetsById[insert.Id];
        }

        var keys = new long[edits.Length];
        for (int i = 0; i < edits.Length; i++) keys[i] = SheetCellKey.Pack(insert.Id, edits[i].Address);
        string[] sheetNames = [insert.Name];

        bool inserted = !forward; // whether the sheet is currently in the workbook
        int touched = 0;
        _rollback.Clear();
        _logWrites = true;
        try
        {
            if (forward)
            {
                InsertSheet(sheet, insert.Index);
                inserted = true;
                for (; touched < edits.Length; touched++)
                    SetEntry(sheet, edits[touched].Address, null, entries[touched]);
            }
            else
            {
                for (; touched < edits.Length; touched++)
                {
                    sheet.Cells.TryGetValue(CellKey.Pack(edits[touched].Address), out entries[touched]);
                    SetEntry(sheet, edits[touched].Address, entries[touched], null);
                }
                RemoveSheet(sheet);
                inserted = false;
            }
            var result = _graph.Recalculate(keys, null, sheetNames, _target, _functions);

            var changed = new long[(forward ? sheet.Cells.Count : 0) + result.ChangedKeys.Count];
            int n = 0;
            if (forward)
                foreach (int key in sheet.Cells.Keys) changed[n++] = SheetCellKey.Pack(sheet.Id, key);
            foreach (long key in result.ChangedKeys) changed[n++] = key;
            return (result, ToSortedAddresses(changed));
        }
        catch
        {
            for (int i = _rollback.Count - 1; i >= 0; i--) _rollback[i].Entry.Value = _rollback[i].OldValue;
            if (forward)
            {
                for (int i = touched - 1; i >= 0; i--) SetEntry(sheet, edits[i].Address, entries[i], null);
                if (inserted) RemoveSheet(sheet);
            }
            else
            {
                if (!inserted) InsertSheet(sheet, insert.Index);
                for (int i = touched - 1; i >= 0; i--) SetEntry(sheet, edits[i].Address, null, entries[i]);
            }
            _active = activeBefore;
            throw;
        }
        finally
        {
            _logWrites = false;
            if (_rollback.Capacity > RollbackRetainCapacity) _rollback = [];
            else _rollback.Clear();
        }
    }

    /// <summary>Sets LastRecalc and raises CellsChanged once with the R81 addresses, unless none changed. Runs after the
    /// history is committed (R25).</summary>
    private void FinishSheetStep(RecalcResult result, string[] changed)
    {
        LastRecalc = new RecalcStats(result.EvaluatedFormulaCount);
        var handler = CellsChanged;
        if (handler is null || changed.Length == 0) return;
        handler(this, new CellsChangedEventArgs(changed));
    }

    /// <summary>R82, §5.5: sheet prefixes print in the sheet's defined casing, or as written when no such sheet exists.</summary>
    private sealed class SheetCasing(Workbook owner) : ISheetCasingResolver
    {
        public string? GetDefinedCasing(string sheet) => owner._sheetsByName.TryGetValue(sheet, out var s) ? s.Name : null;
    }
}
