using System.Globalization;
using System.Text;
using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.IO;

/// <summary>
/// CSV import and export (§8.5, R70). Import detects the encoding, parses with <see cref="DelimitedTextParser"/> and maps
/// fields with <see cref="DelimitedTextMapping"/> (the clipboard-paste rules), then builds the workbook through the load
/// entry point: full recalculation, not dirty, empty undo history, no names. Export writes computed values of the
/// rectangle A1..(last used row, last used column) as UTF-8 with a BOM and CRLF after every record.
/// </summary>
public static class CsvFormat
{
    /// <summary>The most records an imported file may have (the grid height).</summary>
    public const int MaxRecords = Workbook.MaxRows;

    /// <summary>The most fields an imported record may have (columns A..ZZ).</summary>
    public const int MaxFields = Workbook.MaxColumns;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Imports CSV <paramref name="bytes"/> (§8.5). Nothing is built on failure.</summary>
    /// <exception cref="ArgumentException">The delimiter is <c>"</c>, CR or LF (checked first).</exception>
    /// <exception cref="GridlineFormatException">An unterminated quoted field, more than 100,000 records, more than 702
    /// fields in a record, or a stored input longer than 32,767 UTF-16 code units.</exception>
    public static Workbook Import(ReadOnlySpan<byte> bytes, char delimiter = ',')
    {
        DelimitedTextParser.ValidateDelimiter(delimiter);
        // The record and field limits are enforced while parsing, so an oversized file stops at record 100,001 or field
        // 703 instead of materializing every record first (§8.5, resource bound).
        var records = DelimitedTextParser.Parse(Decode(bytes), delimiter, MaxRecords, MaxFields);
        for (int i = 0; i < records.Count; i++)
        {
            foreach (var field in records[i])
                if (DelimitedTextMapping.StoredLength(field) > Workbook.MaxInputLength)
                    throw new GridlineFormatException(
                        $"Record {i + 1}: a field is longer than {Workbook.MaxInputLength} UTF-16 code units once stored.");
        }
        return Workbook.FromCells(DelimitedTextMapping.ToEdits(records, new CellAddress(1, 1)), []);
    }

    /// <summary>Imports the CSV file at <paramref name="path"/> (§8.4 exception mapping, §8.5).</summary>
    /// <exception cref="ArgumentException">The delimiter is invalid (checked before the file is read), or the path is null or empty.</exception>
    /// <exception cref="GridlineIOException">The file cannot be read.</exception>
    /// <exception cref="GridlineFormatException">As <see cref="Import"/>.</exception>
    public static Workbook ImportFromPath(string path, char delimiter = ',')
    {
        DelimitedTextParser.ValidateDelimiter(delimiter);
        return Import(GridlineFile.ReadAllBytes(path), delimiter);
    }

    /// <summary>The CSV bytes of <paramref name="workbook"/> (§8.5): computed values, UTF-8 with BOM, CRLF after every
    /// record; an empty workbook gives only the BOM.</summary>
    /// <exception cref="ArgumentException">The delimiter is <c>"</c>, CR or LF.</exception>
    public static byte[] Export(Workbook workbook, char delimiter = ',')
    {
        ArgumentNullException.ThrowIfNull(workbook);
        DelimitedTextParser.ValidateDelimiter(delimiter);

        // Only used cells carry values; collect them row-major (GetUsedAddresses order) and find the extent.
        var used = workbook.GetUsedAddresses();
        var cells = new (int Row, int Column, CellValue Value)[used.Count];
        int maxColumn = 0;
        for (int i = 0; i < used.Count; i++)
        {
            var a = CellAddress.Parse(used[i]); // GetUsedAddresses gives canonical addresses
            cells[i] = (a.Row, a.Column, workbook.GetValue(used[i]));
            if (a.Column > maxColumn) maxColumn = a.Column;
        }

        using var buffer = new MemoryStream();
        buffer.Write(Encoding.UTF8.Preamble);
        if (cells.Length == 0) return buffer.ToArray();

        int maxRow = cells[^1].Row;
        using (var writer = new StreamWriter(buffer, new UTF8Encoding(false), 1 << 16, leaveOpen: true))
        {
            int next = 0;
            for (int row = 1; row <= maxRow; row++)
            {
                int delimiters = 0; // delimiters written in this record; field c needs c - 1 before it
                while (next < cells.Length && cells[next].Row == row)
                {
                    var (_, column, value) = cells[next++];
                    for (; delimiters < column - 1; delimiters++) writer.Write(delimiter);
                    WriteField(writer, FieldText(value), delimiter);
                }
                for (; delimiters < maxColumn - 1; delimiters++) writer.Write(delimiter);
                writer.Write("\r\n");
            }
        }
        return buffer.ToArray();
    }

    /// <summary>Writes the CSV of <paramref name="workbook"/> to <paramref name="path"/> atomically (§8.4).</summary>
    /// <exception cref="ArgumentException">The delimiter is invalid, or the path is null or empty.</exception>
    /// <exception cref="GridlineIOException">Any I/O or access failure.</exception>
    public static void ExportToPath(Workbook workbook, string path, char delimiter = ',')
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        GridlineFile.WriteAtomic(Export(workbook, delimiter), path);
    }

    private static string FieldText(in CellValue value) => value.Kind switch
    {
        ValueKind.Number => value.Number.ToString("R", CultureInfo.InvariantCulture),
        ValueKind.Boolean => value.Boolean ? "TRUE" : "FALSE",
        ValueKind.Text => value.Text,
        ValueKind.Error => value.Error,
        _ => "",
    };

    private static void WriteField(StreamWriter writer, string field, char delimiter)
    {
        bool quote = field.Length > 0
            && (field[0] == ' ' || field[^1] == ' ' || field.AsSpan().IndexOfAny(delimiter, '"') >= 0
                || field.AsSpan().IndexOfAny('\r', '\n') >= 0);
        if (!quote) { writer.Write(field); return; }
        writer.Write('"');
        writer.Write(field.Replace("\"", "\"\"", StringComparison.Ordinal));
        writer.Write('"');
    }

    /// <summary>Decodes by the §8.5 order: UTF-8 BOM, UTF-16 LE BOM, UTF-16 BE BOM, valid UTF-8, else Windows-1252.</summary>
    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // The provider instance is used directly: no process-wide registration, no static state (§3.3).
            return CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(bytes);
        }
    }
}
