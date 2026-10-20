using System.Globalization;
using Gridline.Core.Addressing;

namespace Gridline.IO;

/// <summary>
/// The §8.5 mapping from parsed delimited fields (<see cref="DelimitedTextParser"/>) to cell inputs, shared by clipboard
/// paste (§11.6) and CSV import. Field j of record i goes to the cell at row i, column j, offset from a top-left cell. An
/// empty field becomes blank; a field starting with <c>=</c> or <c>'</c> is stored as <c>"'" + field</c> (no formula
/// injection, text round-trips); every other field is stored verbatim so §4.3 interprets it.
/// </summary>
public static class DelimitedTextMapping
{
    /// <summary>The cell input for one field (§8.5).</summary>
    public static string ToCellInput(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Length == 0) return "";
        return field[0] is '=' or '\'' ? "'" + field : field;
    }

    /// <summary>The edits that write <paramref name="records"/> with field (0, 0) at <paramref name="topLeft"/>, in record
    /// then field order (row-major), one per field; empty fields give <c>""</c>, which clears the cell.</summary>
    /// <exception cref="ArgumentException">A record would land beyond row 100000 or a field beyond column ZZ.</exception>
    public static IReadOnlyList<KeyValuePair<string, string>> ToEdits(IReadOnlyList<IReadOnlyList<string>> records, CellAddress topLeft)
    {
        ArgumentNullException.ThrowIfNull(records);
        int widest = 0, count = 0;
        foreach (var r in records)
        {
            widest = Math.Max(widest, r.Count);
            count += r.Count;
        }
        long bottom = (long)topLeft.Row + records.Count - 1;
        long right = (long)topLeft.Column + widest - 1;
        if (bottom > GridBounds.MaxRows || right > GridBounds.MaxColumns)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"{records.Count} rows by {widest} columns at {topLeft} exceed the grid ({GridBounds.MaxColumns} columns, {GridBounds.MaxRows} rows)."),
                nameof(records));

        var edits = new KeyValuePair<string, string>[count];
        int k = 0;
        for (int i = 0; i < records.Count; i++)
        {
            var fields = records[i];
            for (int j = 0; j < fields.Count; j++)
                edits[k++] = new(new CellAddress(topLeft.Column + j, topLeft.Row + i).ToString(), ToCellInput(fields[j]));
        }
        return edits;
    }
}
