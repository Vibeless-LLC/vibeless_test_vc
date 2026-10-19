using Gridline.Core;

namespace Gridline.IO;

/// <summary>
/// Byte-exact §8.1 writer (R26, R27): UTF-8 without BOM, LF, trailing LF, two-space indent. Strings are escaped as
/// \" \\ \b \f \n \r \t, other U+0000-U+001F as lowercase \u00xx, lone surrogates as lowercase \udxxx; everything
/// else is written literally. Encodes straight into one growing byte buffer, with no per-cell strings.
/// </summary>
internal sealed class CanonicalWriter
{
    private const string HexDigits = "0123456789abcdef";

    private byte[] _buf;
    private int _len;

    private CanonicalWriter(int capacity) => _buf = new byte[Math.Max(capacity, 256)];

    public static byte[] Write(Workbook workbook, IReadOnlyList<KeyValuePair<string, string>> names)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        ArgumentNullException.ThrowIfNull(names);
        var addresses = workbook.GetUsedAddresses();
        var w = new CanonicalWriter(64 + addresses.Count * 24);
        w.Raw("{\n  \"format\": \"gridline\",\n  \"version\": 1,\n");

        if (names.Count == 0) w.Raw("  \"names\": {},\n");
        else
        {
            var sorted = names.ToArray();
            Array.Sort(sorted, static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key));
            w.Raw("  \"names\": {\n");
            for (int i = 0; i < sorted.Length; i++)
            {
                w.Raw("    ");
                w.Str(sorted[i].Key);
                w.Raw(": ");
                w.Str(sorted[i].Value);
                w.Raw(i + 1 < sorted.Length ? ",\n" : "\n");
            }
            w.Raw("  },\n");
        }

        if (addresses.Count == 0) w.Raw("  \"cells\": []\n");
        else
        {
            w.Raw("  \"cells\": [\n");
            for (int i = 0; i < addresses.Count; i++)
            {
                string a = addresses[i];
                w.Raw("    [");
                w.Str(a);
                w.Raw(", ");
                w.Str(workbook.GetInput(a));
                w.Raw(i + 1 < addresses.Count ? "],\n" : "]\n");
            }
            w.Raw("  ]\n");
        }
        w.Raw("}\n");
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
