using Gridline.Core;

namespace Gridline.IO;

/// <summary>
/// Byte-exact §8.1 writer of format version 2 (CR1.5, R26, R27): UTF-8 without BOM, LF, trailing LF, two-space
/// indent. Strings are escaped as \" \\ \b \f \n \r \t, other U+0000-U+001F as lowercase \u00xx, lone surrogates as
/// lowercase \udxxx; everything else is written literally. Encodes straight into one growing byte buffer, with no per-cell strings.
/// </summary>
internal sealed class CanonicalWriter
{
    private const string HexDigits = "0123456789abcdef";

    private byte[] _buf;
    private int _len;

    private CanonicalWriter(int capacity) => _buf = new byte[Math.Max(capacity, 256)];

    /// <summary>
    /// The version 2 canonical bytes (CR1.5, §8.1): keys format, version, activeSheet, names, sheets; names sorted
    /// OrdinalIgnoreCase with their qualified refersTo (R77) or #REF! (R59); sheets in workbook order, each with keys
    /// name, cells and its cells row-major with unqualified addresses; empty forms <c>"names": {}</c> and
    /// <c>"cells": []</c>; two-space indent per nesting level.
    /// </summary>
    public static byte[] Write(Workbook workbook)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        var names = workbook.GetNames(); // already sorted OrdinalIgnoreCase (§9.6)
        var sheetNames = workbook.SheetNames;
        var w = new CanonicalWriter(128 + names.Count * 48 + sheetNames.Count * 64 + workbook.UsedCellCount * 24);
        w.Raw("{\n  \"format\": \"gridline\",\n  \"version\": 2,\n  \"activeSheet\": ");
        w.Str(workbook.ActiveSheet);
        w.Raw(",\n");

        if (names.Count == 0) w.Raw("  \"names\": {},\n");
        else
        {
            w.Raw("  \"names\": {\n");
            for (int i = 0; i < names.Count; i++)
            {
                w.Raw("    ");
                w.Str(names[i].Key);
                w.Raw(": ");
                w.Str(names[i].Value);
                w.Raw(i + 1 < names.Count ? ",\n" : "\n");
            }
            w.Raw("  },\n");
        }

        w.Raw("  \"sheets\": [\n"); // a workbook always has at least one sheet (CR1.1), so there is no empty form
        for (int s = 0; s < sheetNames.Count; s++)
        {
            w.Raw("    {\n      \"name\": ");
            w.Str(sheetNames[s]);
            w.Raw(",\n");
            var cells = workbook.GetSheetCells(s);
            if (cells.Length == 0) w.Raw("      \"cells\": []\n");
            else
            {
                w.Raw("      \"cells\": [\n");
                for (int i = 0; i < cells.Length; i++)
                {
                    w.Raw("        [");
                    w.Str(cells[i].Key);
                    w.Raw(", ");
                    w.Str(cells[i].Value);
                    w.Raw(i + 1 < cells.Length ? "],\n" : "]\n");
                }
                w.Raw("      ]\n");
            }
            w.Raw(s + 1 < sheetNames.Count ? "    },\n" : "    }\n");
        }
        w.Raw("  ]\n}\n");
        return w._buf.AsSpan(0, w._len).ToArray();
    }

    private void Ensure(int extra)
    {
        if (_len + extra <= _buf.Length) return;
        Array.Resize(ref _buf, (int)Math.Min(Array.MaxLength, Math.Max((long)_buf.Length * 2, (long)_len + extra)));
    }

    /// <summary>Appends ASCII-only text.</summary>
    private void Raw(string ascii)
    {
        Ensure(ascii.Length);
        foreach (char c in ascii) _buf[_len++] = (byte)c;
    }

    private void Str(string s)
    {
        Ensure(s.Length * 6 + 2); // worst case: one \uXXXX escape per code unit
        var buf = _buf;
        int n = _len;
        buf[n++] = (byte)'"';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= 0x20 && c < 0x80)
            {
                if (c == '"' || c == '\\') buf[n++] = (byte)'\\';
                buf[n++] = (byte)c;
            }
            else if (c < 0x20)
            {
                buf[n++] = (byte)'\\';
                switch (c)
                {
                    case '\b': buf[n++] = (byte)'b'; break;
                    case '\f': buf[n++] = (byte)'f'; break;
                    case '\n': buf[n++] = (byte)'n'; break;
                    case '\r': buf[n++] = (byte)'r'; break;
                    case '\t': buf[n++] = (byte)'t'; break;
                    default:
                        buf[n++] = (byte)'u'; buf[n++] = (byte)'0'; buf[n++] = (byte)'0';
                        buf[n++] = (byte)HexDigits[c >> 4]; buf[n++] = (byte)HexDigits[c & 0xF];
                        break;
                }
            }
            else if (c < 0x800)
            {
                buf[n++] = (byte)(0xC0 | (c >> 6));
                buf[n++] = (byte)(0x80 | (c & 0x3F));
            }
            else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                int cp = char.ConvertToUtf32(c, s[++i]);
                buf[n++] = (byte)(0xF0 | (cp >> 18));
                buf[n++] = (byte)(0x80 | ((cp >> 12) & 0x3F));
                buf[n++] = (byte)(0x80 | ((cp >> 6) & 0x3F));
                buf[n++] = (byte)(0x80 | (cp & 0x3F));
            }
            else if (char.IsSurrogate(c))
            {
                buf[n++] = (byte)'\\'; buf[n++] = (byte)'u';
                buf[n++] = (byte)HexDigits[(c >> 12) & 0xF]; buf[n++] = (byte)HexDigits[(c >> 8) & 0xF];
                buf[n++] = (byte)HexDigits[(c >> 4) & 0xF]; buf[n++] = (byte)HexDigits[c & 0xF];
            }
            else
            {
                buf[n++] = (byte)(0xE0 | (c >> 12));
                buf[n++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                buf[n++] = (byte)(0x80 | (c & 0x3F));
            }
        }
        buf[n++] = (byte)'"';
        _len = n;
    }
}
