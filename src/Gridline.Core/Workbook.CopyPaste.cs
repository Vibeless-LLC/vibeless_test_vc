using System.Collections.Immutable;
using Gridline.Core.Addressing;
using Gridline.Core.Commands;
using Gridline.Core.Dependencies;
using Gridline.Core.Parsing;

namespace Gridline.Core;

// CopyPaste (§9.4): builds one CellEdit list and records it through the shared mutation path (Workbook.Edits.cs).
public sealed partial class Workbook
{
    /// <summary>
    /// Copies <paramref name="sourceRange"/> to the same-sized rectangle whose top-left is
    /// <paramref name="destinationTopLeft"/> as one mutation and one undo step (§9.4, §9.3). The source is snapshotted
    /// first, so overlapping rectangles work. Constant inputs are copied verbatim and blank source cells clear their
    /// destination; every formula is re-emitted canonically (§5.5) with its relative reference components shifted by
    /// the offset ($ components stay; off-grid references become #REF!). A formula that failed to parse is copied
    /// verbatim (R39). Edits that leave an input unchanged are dropped; if none remain the call is a no-op with no step
    /// and no event (LastRecalc becomes 0, R24). Work is proportional to used cells, not to the rectangle's area.
    /// </summary>
    /// <param name="sourceRange">A range (corners in any order) or a single cell address, $ markers allowed (§4.2).</param>
    /// <param name="destinationTopLeft">A single cell address.</param>
    /// <remarks>Both addresses may be qualified (R83); unqualified means the active sheet. The source range has one sheet.</remarks>
    /// <exception cref="ArgumentException">An argument is null or invalid or names an unknown sheet, the destination
    /// rectangle exceeds the grid, or a rewritten formula would be longer than <see cref="MaxInputLength"/> (R55). Nothing changes.</exception>
    /// <exception cref="NotSupportedException">The source and destination are on different sheets (not yet supported, D-52).</exception>
    public void CopyPaste(string sourceRange, string destinationTopLeft)
    {
        ArgumentNullException.ThrowIfNull(sourceRange);
        var sheet = SplitSheet(sourceRange, nameof(sourceRange), out string sourceText);
        var source = ParseRangeOrCell(sourceText, sourceRange, nameof(sourceRange));
        var (destSheet, dest) = ResolveAddress(destinationTopLeft, nameof(destinationTopLeft));
        if (destSheet != sheet)
            throw new NotSupportedException("Copying between sheets is not supported yet; nothing was changed.");
        int bottom = dest.Row + (source.BottomRight.Row - source.TopLeft.Row);
        int right = dest.Column + (source.BottomRight.Column - source.TopLeft.Column);
        if (bottom > MaxRows || right > MaxColumns)
            throw new ArgumentException(
                $"Pasting {source} at {dest} exceeds the grid ({MaxColumns} columns, {MaxRows} rows).", nameof(destinationTopLeft));
        var target = new CellRange(dest, new CellAddress(right, bottom));
        int rowOffset = dest.Row - source.TopLeft.Row;
        int columnOffset = dest.Column - source.TopLeft.Column;

        // Snapshot (pure): the new input of every destination cell whose source cell is used.
        var shifter = new ReferenceShifter(rowOffset, columnOffset);
        var pasted = new Dictionary<int, (CellAddress Cell, string Input)>();
        foreach (int key in UsedKeysIn(sheet.Cells, source))
        {
            var entry = sheet.Cells[key];
            var to = new CellAddress(CellKey.Column(key) + columnOffset, CellKey.Row(key) + rowOffset);
            string input = entry.Formula?.Root is { } root ? FormulaPrinter.Print(root, shifter, _nameCasing, _sheetCasing) : entry.Input; // R39, §5.5 name and sheet casing
            if (input.Length > MaxInputLength)
                throw new ArgumentException(
                    $"The formula pasted to {to} would exceed {MaxInputLength} UTF-16 code units.", nameof(sourceRange)); // R55
            pasted.Add(CellKey.Pack(to), (to, input));
        }

        var changes = ImmutableArray.CreateBuilder<CellEdit>(pasted.Count);
        foreach (var (key, (cell, input)) in pasted)
        {
            string current = sheet.Cells.TryGetValue(key, out var e) ? e.Input : "";
            if (!string.Equals(current, input, StringComparison.Ordinal))
                changes.Add(new CellEdit(cell, current, input) { Sheet = sheet.Id });
        }
        foreach (int key in UsedKeysIn(sheet.Cells, target)) // used destination cells under a blank source cell are cleared
            if (!pasted.ContainsKey(key))
                changes.Add(new CellEdit(CellKey.Unpack(key), sheet.Cells[key].Input, "") { Sheet = sheet.Id });

        if (changes.Count == 0)
        {
            LastRecalc = default; // R24
            return;
        }
        Perform(new UndoStep(changes.DrainToImmutable())); // each address once: pasted keys are unique, clears skip them
    }

    private static CellRange ParseRangeOrCell(string text, string original, string paramName)
    {
        if (CellRange.TryParse(text, out var range)) return range;
        if (CellAddress.TryParse(text, out var cell)) return new CellRange(cell, cell);
        throw new ArgumentException($"Invalid cell range: '{Clip(original)}'.", paramName);
    }

    /// <summary>Keys of the used cells inside <paramref name="range"/>: probes each address when the range is smaller
    /// than the used set, otherwise filters the used set, so the cost is O(min(area, used cells)).</summary>
    private static List<int> UsedKeysIn(Dictionary<int, Entry> cells, CellRange range)
    {
        var keys = new List<int>();
        if (range.CellCount <= cells.Count)
        {
            for (int c = range.TopLeft.Column; c <= range.BottomRight.Column; c++)
                for (int r = range.TopLeft.Row; r <= range.BottomRight.Row; r++)
                {
                    int key = CellKey.Pack(c, r);
                    if (cells.ContainsKey(key)) keys.Add(key);
                }
        }
        else
        {
            foreach (int key in cells.Keys)
            {
                int c = CellKey.Column(key), r = CellKey.Row(key);
                if (c >= range.TopLeft.Column && c <= range.BottomRight.Column
                    && r >= range.TopLeft.Row && r <= range.BottomRight.Row) keys.Add(key);
            }
        }
        return keys;
    }
}
