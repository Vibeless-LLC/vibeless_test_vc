using System.Text;
using System.Text.Json;
using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.IO;

/// <summary>
/// Strict §8.2 loader (R27) of format versions 1 and 2 (CR1.5, R86). Pass 1 walks the document once (syntax, duplicate
/// keys, top-level shape); the "cells" (version 1) or "sheets" (version 2) array is validated and collected only after
/// the version/format checks, so an unsupported version always reports as such. Nothing is built until every entry is
/// valid; the workbook is created by a single <see cref="Workbook.FromSheets"/>. A version 1 file migrates to one sheet
/// Sheet1 with its names qualified with Sheet1 (CR1.5).
/// </summary>
internal static class StrictLoader
{
    private const int MaxDepth = 64;

    /// <summary>The newest format version this build reads (CR1.5).</summary>
    private const int MaxVersion = 2;

    public static Workbook Load(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data[3..];
        // Utf8JsonReader only decodes strings it is asked for, so skipped values would let bad bytes through.
        if (!System.Text.Unicode.Utf8.IsValid(data)) throw Fail("the file is not valid UTF-8.");
        try
        {
            return Parse(data);
        }
        catch (GridlineFormatException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            // Syntax/UTF-8/depth errors from the reader. Anything else (a Core bug) propagates rather than being
            // disguised as a format error.
            throw new GridlineFormatException("Invalid .gridline file: " + ex.Message, ex);
        }
    }

    private static GridlineFormatException Fail(string message) => new("Invalid .gridline file: " + message);

    private static Workbook Parse(ReadOnlySpan<byte> data)
    {
        var reader = new Utf8JsonReader(data, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaxDepth,
        });

        if (!reader.Read()) throw Fail("the file is empty.");
        if (reader.TokenType != JsonTokenType.StartObject) throw Fail("the root must be a JSON object.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? problem = null;
        bool formatOk = false, hasFormat = false, hasVersion = false;
        bool hasCells = false, cellsIsArray = false, hasSheets = false, sheetsIsArray = false, hasActiveSheet = false;
        long version = 0;
        bool versionHuge = false;
        string? activeSheet = null;
        Utf8JsonReader cellsReader = default, sheetsReader = default;
        var names = new List<KeyValuePair<string, string>>();

        while (true)
        {
            if (!reader.Read()) throw Fail("unexpected end of file.");
            if (reader.TokenType == JsonTokenType.EndObject) break;
            string key = ReadString(ref reader);
            if (!seen.Add(key)) throw Fail($"duplicate key \"{key}\".");
            if (!reader.Read()) throw Fail("unexpected end of file.");

            switch (key)
            {
                case "format":
                    hasFormat = true;
                    if (reader.TokenType == JsonTokenType.String) formatOk = ReadString(ref reader) == "gridline";
                    else SkipValue(ref reader);
                    break;

                case "version":
                    hasVersion = true;
                    if (reader.TokenType != JsonTokenType.Number)
                    {
                        problem ??= "\"version\" must be an integer.";
                        SkipValue(ref reader);
                    }
                    else
                    {
                        // R54, R86 (§8.2): any integral JSON number is an integer, so 1.0 and 1e0 are version 1 and
                        // 2.0 is version 2; 1.5 is not an integer.
                        var raw = reader.ValueSpan;
                        if (reader.TryGetInt64(out long v)) version = v;
                        else if (!reader.TryGetDouble(out double d) || !double.IsFinite(d))
                            versionHuge = raw.Length > 0 && raw[0] != (byte)'-'; // beyond double range
                        else if (d != Math.Floor(d)) problem ??= "\"version\" must be an integer.";
                        else if (d > MaxVersion) versionHuge = true;
                        else if (d < 1) problem ??= "\"version\" must be 1 or 2.";
                        else version = (long)d;
                    }
                    break;

                case "names":
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        // §8.2: each entry is name -> refersTo string. Syntax and exact-duplicate keys are checked here;
                        // name/refersTo validity and case-insensitive duplicates are checked by Core after the
                        // version/format checks (so an unsupported version reports first).
                        var nameKeys = new HashSet<string>(StringComparer.Ordinal);
                        while (true)
                        {
                            if (!reader.Read()) throw Fail("unexpected end of file.");
                            if (reader.TokenType == JsonTokenType.EndObject) break;
                            string nk = ReadString(ref reader);
                            if (!nameKeys.Add(nk)) throw Fail($"duplicate key \"{nk}\".");
                            if (!reader.Read()) throw Fail("unexpected end of file.");
                            if (reader.TokenType == JsonTokenType.String) names.Add(new(nk, ReadString(ref reader)));
                            else
                            {
                                problem ??= $"the value of name \"{nk}\" must be a string.";
                                SkipValue(ref reader);
                            }
                        }
                    }
                    else
                    {
                        problem ??= "\"names\" must be an object.";
                        SkipValue(ref reader);
                    }
                    break;

                // "cells" belongs to version 1 and "activeSheet"/"sheets" to version 2 (CR1.5). Each is recorded here
                // and judged once the version is known (R86).
                case "cells":
                    hasCells = true;
                    cellsIsArray = reader.TokenType == JsonTokenType.StartArray;
                    if (cellsIsArray) cellsReader = reader; // entries are checked later
                    SkipValue(ref reader);
                    break;

                case "activeSheet":
                    hasActiveSheet = true;
                    if (reader.TokenType == JsonTokenType.String) activeSheet = ReadString(ref reader);
                    else SkipValue(ref reader);
                    break;

                case "sheets":
                    hasSheets = true;
                    sheetsIsArray = reader.TokenType == JsonTokenType.StartArray;
                    if (sheetsIsArray) sheetsReader = reader; // sheets are checked later
                    SkipValue(ref reader);
                    break;

                default:
                    SkipValue(ref reader); // unknown top-level keys are ignored (still syntax- and duplicate-checked)
                    break;
            }
        }

        if (reader.Read()) throw Fail("unexpected content after the root object.");

        if (versionHuge || version > MaxVersion) throw Fail("unsupported version; this build reads versions 1 and 2.");
        if (!hasFormat || !formatOk) throw Fail("\"format\" must be \"gridline\".");
        if (!hasVersion) throw Fail("\"version\" is missing.");
        if (problem is not null) throw Fail(problem);
        if (version < 1) throw Fail("\"version\" must be 1 or 2.");

        if (version == 1)
        {
            // R86: the version 2 keys are not allowed in a version 1 file.
            if (hasActiveSheet) throw Fail("a version 1 file cannot contain \"activeSheet\".");
            if (hasSheets) throw Fail("a version 1 file cannot contain \"sheets\".");
            if (hasCells && !cellsIsArray) throw Fail("\"cells\" must be an array.");
            // R54 (§8.2): a missing "names" or "cells" key counts as empty (names stays an empty list, cells is []).
            // CR1.5: the file migrates to one sheet Sheet1; its names must be unqualified and are qualified with Sheet1.
            var cells = hasCells ? ReadCells(ref cellsReader, "") : [];
            return Build([new LoadedSheet(Workbook.DefaultSheetName, cells)], Workbook.DefaultSheetName, names,
                LoadedNameForm.Unqualified);
        }

        // Version 2 (CR1.5, R86): "format", "version", "activeSheet" and "sheets" are required; a missing "names" is
        // empty (R54). A top-level "cells" is not a version 2 key, so it is ignored like any unknown key.
        if (!hasActiveSheet) throw Fail("\"activeSheet\" is missing.");
        if (activeSheet is null) throw Fail("\"activeSheet\" must be a string.");
        if (!hasSheets) throw Fail("\"sheets\" is missing.");
        if (!sheetsIsArray) throw Fail("\"sheets\" must be an array.");
        var sheets = ReadSheets(ref sheetsReader);
        if (sheets.Count == 0) throw Fail("\"sheets\" must contain at least one sheet.");
        return Build(sheets, activeSheet, names, LoadedNameForm.Qualified);
    }

    /// <summary>The single Core call that builds the workbook; everything is validated before anything is built.</summary>
    private static Workbook Build(IReadOnlyList<LoadedSheet> sheets, string activeSheet,
        List<KeyValuePair<string, string>> names, LoadedNameForm nameForm)
    {
        try
        {
            return Workbook.FromSheets(sheets, activeSheet, names, functions: null, nameForm);
        }
        catch (ArgumentException ex)
        {
            // FromSheets rejected an entry: a duplicate address, invalid input, an invalid or duplicate name, a refersTo
            // in the wrong form or naming an unknown sheet, an invalid or duplicate sheet name, too many sheets, or an
            // unknown active sheet (R5, §9.6, CR1.5, R86). Only this call is mapped.
            throw new GridlineFormatException("Invalid .gridline file: " + ex.Message, ex);
        }
    }

    /// <summary>The "sheets" array of a version 2 file (CR1.5, R86), the reader at its StartArray. Each entry is an
    /// object with a string "name" (required) and a "cells" array (a missing "cells" is empty, as R54 rules for version
    /// 1); other keys are ignored. Sheet names, duplicates, the sheet count and the active sheet are checked by Core.</summary>
    private static List<LoadedSheet> ReadSheets(ref Utf8JsonReader reader)
    {
        var sheets = new List<LoadedSheet>();
        int index = 0;
        while (true)
        {
            if (!reader.Read()) throw Fail("unexpected end of file.");
            if (reader.TokenType == JsonTokenType.EndArray) return sheets;
            if (reader.TokenType != JsonTokenType.StartObject) throw Fail($"sheet {index} must be an object.");

            string? name = null;
            List<KeyValuePair<string, string>>? cells = null;
            while (true) // duplicate keys were already rejected by the first pass (SkipValue)
            {
                if (!reader.Read()) throw Fail("unexpected end of file.");
                if (reader.TokenType == JsonTokenType.EndObject) break;
                string key = ReadString(ref reader);
                if (!reader.Read()) throw Fail("unexpected end of file.");
                switch (key)
                {
                    case "name":
                        if (reader.TokenType != JsonTokenType.String) throw Fail($"sheet {index}: \"name\" must be a string.");
                        name = ReadString(ref reader);
                        break;
                    case "cells":
                        if (reader.TokenType != JsonTokenType.StartArray) throw Fail($"sheet {index}: \"cells\" must be an array.");
                        cells = ReadCells(ref reader, $"sheet {index}: ");
                        break;
                    default:
                        SkipValue(ref reader);
                        break;
                }
            }
            if (name is null) throw Fail($"sheet {index}: \"name\" is missing.");
            sheets.Add(new LoadedSheet(name, cells ?? []));
            index++;
        }
    }

    private static List<KeyValuePair<string, string>> ReadCells(ref Utf8JsonReader reader, string context)
    {
        var cells = new List<KeyValuePair<string, string>>();
        int index = 0;
        while (true)
        {
            if (!reader.Read()) throw Fail("unexpected end of file.");
            if (reader.TokenType == JsonTokenType.EndArray) return cells;
            if (reader.TokenType != JsonTokenType.StartArray) throw Fail($"{context}cell entry {index} must be an array [address, input].");

            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                throw Fail($"{context}cell entry {index} must be a two-element array of strings.");
            string address = ReadString(ref reader);
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                throw Fail($"{context}cell entry {index} must be a two-element array of strings.");
            string input = ReadString(ref reader);
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                throw Fail($"{context}cell entry {index} must have exactly two elements.");

            // §8.2/§4.2: any valid address is accepted (case-insensitive, optional `$`); FromSheets stores it
            // canonically and rejects duplicates compared canonically (so "A1" and "a1" are duplicates). Addresses are
            // never sheet-qualified: the cells of a version 2 sheet lie on that sheet (R86).
            if (!CellAddress.TryParse(address, out _))
                throw Fail(address.Contains('!')
                    ? $"{context}cell entry {index}: \"{address}\" is sheet-qualified; cell addresses in a file are unqualified."
                    : $"{context}cell entry {index}: \"{address}\" is not a valid cell address.");
            // R54 (§8.2): an empty input is a blank cell; FromSheets skips it but still counts it for duplicates.
            if (input.Length > Workbook.MaxInputLength)
                throw Fail($"{context}cell {address} input exceeds {Workbook.MaxInputLength} characters.");

            cells.Add(new KeyValuePair<string, string>(address, input));
            index++;
        }
    }

    /// <summary>Consumes the value starting at the current token (its whole subtree), rejecting duplicate keys in any object.</summary>
    private static void SkipValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartArray:
                while (true)
                {
                    if (!reader.Read()) throw Fail("unexpected end of file.");
                    if (reader.TokenType == JsonTokenType.EndArray) return;
                    SkipValue(ref reader);
                }
            case JsonTokenType.StartObject:
                var keys = new HashSet<string>(StringComparer.Ordinal);
                while (true)
                {
                    if (!reader.Read()) throw Fail("unexpected end of file.");
                    if (reader.TokenType == JsonTokenType.EndObject) return;
                    string key = ReadString(ref reader);
                    if (!keys.Add(key)) throw Fail($"duplicate key \"{key}\".");
                    if (!reader.Read()) throw Fail("unexpected end of file.");
                    SkipValue(ref reader);
                }
            default:
                return; // scalar: already consumed
        }
    }

    /// <summary>
    /// The current String/PropertyName token as a .NET string. Escapes are decoded here (not by GetString) so that
    /// lone-surrogate escapes such as backslash-u-d800, which the writer emits (R27), load back losslessly.
    /// </summary>
    private static string ReadString(ref Utf8JsonReader reader)
    {
        if (!reader.ValueIsEscaped) return reader.GetString()!;
        var raw = reader.ValueSpan;
        var sb = new StringBuilder(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            int run = raw[i..].IndexOf((byte)'\\');
            if (run < 0) run = raw.Length - i;
            if (run > 0)
            {
                sb.Append(Encoding.UTF8.GetString(raw.Slice(i, run)));
                i += run;
                continue;
            }
            // raw[i] is a backslash; the reader has already validated the escape syntax.
            byte e = raw[i + 1];
            i += 2;
            switch (e)
            {
                case (byte)'"': sb.Append('"'); break;
                case (byte)'\\': sb.Append('\\'); break;
                case (byte)'/': sb.Append('/'); break;
                case (byte)'b': sb.Append('\b'); break;
                case (byte)'f': sb.Append('\f'); break;
                case (byte)'n': sb.Append('\n'); break;
                case (byte)'r': sb.Append('\r'); break;
                case (byte)'t': sb.Append('\t'); break;
                case (byte)'u':
                    int code = 0;
                    for (int k = 0; k < 4; k++) code = (code << 4) | HexValue(raw[i + k]);
                    sb.Append((char)code);
                    i += 4;
                    break;
                default: throw Fail("invalid escape sequence.");
            }
        }
        return sb.ToString();
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => throw Fail("invalid unicode escape."),
    };
}
