using System.Text;
using System.Text.Json;
using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.IO;

/// <summary>
/// Strict §8.2 loader (R27). Pass 1 walks the document once (syntax, duplicate keys, top-level shape); the "cells"
/// array is validated and collected only after the version/format checks, so an unsupported version always reports as
/// such. Nothing is built until every entry is valid; the workbook is created by a single <see cref="Workbook.FromCells"/>.
/// </summary>
internal static class StrictLoader
{
    private const int MaxDepth = 64;

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
        bool formatOk = false, hasFormat = false, hasVersion = false, hasCells = false;
        long version = 0;
        bool versionHuge = false;
        Utf8JsonReader cellsReader = default;
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
                        // R54 (§8.2): any integral JSON number is an integer, so 1.0 and 1e0 are version 1; 1.5 is not.
                        var raw = reader.ValueSpan;
                        if (reader.TryGetInt64(out long v)) version = v;
                        else if (!reader.TryGetDouble(out double d) || !double.IsFinite(d))
                            versionHuge = raw.Length > 0 && raw[0] != (byte)'-'; // beyond double range
                        else if (d != Math.Floor(d)) problem ??= "\"version\" must be an integer.";
                        else if (d > 1) versionHuge = true;
                        else if (d < 1) problem ??= "\"version\" must be 1.";
                        else version = 1;
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

                case "cells":
                    hasCells = true;
                    if (reader.TokenType == JsonTokenType.StartArray) cellsReader = reader; // entries are checked later
                    else problem ??= "\"cells\" must be an array.";
                    SkipValue(ref reader);
                    break;

                default:
                    SkipValue(ref reader); // unknown top-level keys are ignored (still syntax- and duplicate-checked)
                    break;
            }
        }

        if (reader.Read()) throw Fail("unexpected content after the root object.");

        if (versionHuge || version > 1) throw Fail("unsupported version; this build reads version 1.");
        if (!hasFormat || !formatOk) throw Fail("\"format\" must be \"gridline\".");
        if (!hasVersion) throw Fail("\"version\" is missing.");
        if (problem is not null) throw Fail(problem);
        if (version < 1) throw Fail("\"version\" must be 1.");

        // R54 (§8.2): a missing "names" or "cells" key counts as empty (names stays an empty list, cells is []).
        var cells = hasCells ? ReadCells(ref cellsReader) : [];
        try
        {
            return Workbook.FromCells(cells, names);
        }
        catch (ArgumentException ex)
        {
            // FromCells rejected an entry (duplicate, invalid input, invalid or duplicate name; R5, §9.6). Only this call is mapped.
            throw new GridlineFormatException("Invalid .gridline file: " + ex.Message, ex);
        }
    }

    private static List<KeyValuePair<string, string>> ReadCells(ref Utf8JsonReader reader)
    {
        var cells = new List<KeyValuePair<string, string>>();
        int index = 0;
        while (true)
        {
            if (!reader.Read()) throw Fail("unexpected end of file.");
            if (reader.TokenType == JsonTokenType.EndArray) return cells;
            if (reader.TokenType != JsonTokenType.StartArray) throw Fail($"cell entry {index} must be an array [address, input].");

            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                throw Fail($"cell entry {index} must be a two-element array of strings.");
            string address = ReadString(ref reader);
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                throw Fail($"cell entry {index} must be a two-element array of strings.");
            string input = ReadString(ref reader);
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                throw Fail($"cell entry {index} must have exactly two elements.");

            // §8.2/§4.2: any valid address is accepted (case-insensitive, optional `$`); FromCells stores it canonically
            // and rejects duplicates compared canonically (so "A1" and "a1" are duplicates).
            if (!CellAddress.TryParse(address, out _))
                throw Fail($"cell entry {index}: \"{address}\" is not a valid cell address.");
            // R54 (§8.2): an empty input is a blank cell; FromCells skips it but still counts it for duplicates.
            if (input.Length > Workbook.MaxInputLength) throw Fail($"cell {address} input exceeds {Workbook.MaxInputLength} characters.");

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
