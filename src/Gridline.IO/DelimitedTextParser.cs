using System.Globalization;
using System.Text;

namespace Gridline.IO;

/// <summary>
/// Splits delimited text into records and fields by the §8.5 import rules (used by clipboard paste with Tab, §11.6, and
/// by CSV import). Records are separated by CRLF, LF or CR in any mix, and a final terminator does not create an empty
/// record. Fields follow RFC 4180 with the given delimiter: a field that starts with <c>"</c> is quoted and may contain
/// the delimiter, <c>""</c> (one quote) and line breaks (kept as written); a <c>"</c> inside an unquoted field is a
/// literal character. Text after a quoted field's closing quote, up to the next delimiter or terminator, is appended
/// literally (lenient, D-39). Encoding detection is the caller's concern: this type works on a decoded string.
/// </summary>
public static class DelimitedTextParser
{
    /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="delimiter"/> is usable (§8.5: any character
    /// except <c>"</c>, CR and LF).</summary>
    public static void ValidateDelimiter(char delimiter)
    {
        if (delimiter is '"' or '\r' or '\n')
            throw new ArgumentException("The delimiter cannot be '\"', CR or LF.", nameof(delimiter));
    }

    /// <summary>Parses <paramref name="text"/> into records of fields. Empty text gives no records; a record may have
    /// any number of fields (at least one).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException">The delimiter is <c>"</c>, CR or LF.</exception>
    /// <exception cref="GridlineFormatException">A quoted field is not terminated before the end of the text.</exception>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text, char delimiter) =>
        Parse(text, delimiter, int.MaxValue, int.MaxValue);

    /// <summary>As <see cref="Parse(string, char)"/>, but enforces the limits while parsing (CSV import, §8.5): it stops
    /// and throws as soon as record <paramref name="maxRecords"/> + 1 begins or a record reaches field
    /// <paramref name="maxFields"/> + 1, so an oversized input costs no more than the limits allow beyond the text itself.</summary>
    /// <exception cref="GridlineFormatException">A quoted field is not terminated, or a limit is exceeded.</exception>
    internal static IReadOnlyList<IReadOnlyList<string>> Parse(string text, char delimiter, int maxRecords, int maxFields)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateDelimiter(delimiter);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecords, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFields, 1);

        var records = new List<IReadOnlyList<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        int i = 0, n = text.Length;
        int record = 1;
        while (i < n)
        {
            // At the start of a field.
            if (text[i] == '"')
            {
                int start = i++;
                while (true)
                {
                    if (i >= n)
                        throw new GridlineFormatException(
                            $"Record {record}: the quoted field starting at character {start + 1} is not terminated.");
                    char c = text[i++];
                    if (c != '"') { field.Append(c); continue; }
                    if (i < n && text[i] == '"') { field.Append('"'); i++; continue; } // "" is one quote
                    break; // closing quote
                }
            }
            // Unquoted characters (or the remainder after a closing quote) up to the delimiter or a terminator.
            while (i < n && text[i] != delimiter && text[i] != '\r' && text[i] != '\n') field.Append(text[i++]);

            AddField(field.ToString());
            field.Clear();
            if (i >= n) break; // end of text ends the last record
            char end = text[i++];
            if (end == delimiter)
            {
                if (i >= n) AddField(""); // a trailing delimiter ends with an empty field
                continue;
            }
            if (end == '\r' && i < n && text[i] == '\n') i++; // CRLF is one terminator
            records.Add(fields.ToArray());
            fields.Clear();
            record++;
            if (records.Count == maxRecords && i < n) // a final terminator starts no record; more text does
                throw new GridlineFormatException(string.Create(CultureInfo.InvariantCulture,
                    $"The file has more than {maxRecords} records; the limit is {maxRecords}."));
        }
        if (fields.Count > 0) records.Add(fields.ToArray()); // the last record had no terminator
        return records;

        void AddField(string value)
        {
            if (fields.Count == maxFields)
                throw new GridlineFormatException(string.Create(CultureInfo.InvariantCulture,
                    $"Record {record} has more than {maxFields} fields; the limit is {maxFields}."));
            fields.Add(value);
        }
    }
}
