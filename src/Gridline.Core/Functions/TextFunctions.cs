using System.Text;
using Gridline.Core.Evaluation;

namespace Gridline.Core.Functions;

/// <summary>
/// The text functions of §7.3 (rulings R14, R16, R19). Registered by <see cref="Register"/>.
/// </summary>
/// <remarks>
/// Positions and lengths are UTF-16 code units. Text parameters coerce through <see cref="Coercion"/> (numbers via the
/// G15 rule, booleans "TRUE"/"FALSE", blank ""); a multi-cell range in a scalar parameter is #VALUE!. Integer
/// parameters truncate toward zero (§6.9). Errors propagate, first in argument order. A text result longer than
/// <see cref="Coercion.MaxTextLength"/> is #VALUE!, checked before the string is built.
/// SEARCH wildcards are matched without backtracking recursion: the pattern is split on <c>*</c> into segments (each
/// made of literals and <c>?</c>), and segments are located greedily left to right. If the leftmost placement of the
/// first segment cannot be completed, no later placement can be, so the whole search is O(n * m).
/// </remarks>
internal static class TextFunctions
{
    private static readonly CellValue ValueError = CellValue.FromError(ErrorCodes.Value);
    private static readonly CellValue EmptyText = CellValue.FromText("");

    public static void Register(BuiltInFunctionTable.Builder b)
    {
        const int Max = BuiltInFunctionTable.MaxVariadicArguments;
        Add(b, "LEN", 1, 1, Len);
        Add(b, "LEFT", 1, 2, a => Edge(a, fromLeft: true));
        Add(b, "RIGHT", 1, 2, a => Edge(a, fromLeft: false));
        Add(b, "MID", 3, 3, Mid);
        Add(b, "UPPER", 1, 1, a => Map(a, s => s.ToUpperInvariant()));
        Add(b, "LOWER", 1, 1, a => Map(a, s => s.ToLowerInvariant()));
        Add(b, "PROPER", 1, 1, a => Map(a, Proper));
        Add(b, "TRIM", 1, 1, a => Map(a, Trim));
        Add(b, "CONCAT", 1, Max, Concat);
        Add(b, "TEXTJOIN", 3, Max, TextJoin);
        Add(b, "FIND", 2, 3, a => Locate(a, wildcards: false));
        Add(b, "SEARCH", 2, 3, a => Locate(a, wildcards: true));
        Add(b, "SUBSTITUTE", 3, 4, Substitute);
        Add(b, "REPLACE", 4, 4, Replace);
        Add(b, "REPT", 2, 2, Rept);
        Add(b, "EXACT", 2, 2, Exact);
        Add(b, "VALUE", 1, 1, Value);
    }

    private static void Add(BuiltInFunctionTable.Builder b, string name, int min, int max, Func<IFunctionArguments, CellValue> body) =>
        b.Add(name, min, max, new DelegateFunction(body));

    // ---- argument helpers ----

    private static bool TryText(IFunctionArguments a, int index, out string text, out CellValue error)
    {
        var v = a.Evaluate(index);
        if (v.Kind == ValueKind.Error) { text = ""; error = v; return false; }
        text = Coercion.ScalarToText(v);
        error = default;
        return true;
    }

    /// <summary>Integer parameter (§6.9): coerced to a number and truncated toward zero, clamped to +/-4e9.</summary>
    private static bool TryInt(IFunctionArguments a, int index, out long value, out CellValue error)
    {
        value = 0;
        if (!Coercion.TryToNumber(a.Evaluate(index), out var n, out error)) return false;
        value = (long)Math.Clamp(Math.Truncate(n), -4e9, 4e9);
        return true;
    }

    private static CellValue Text(string s) => s.Length == 0 ? EmptyText : CellValue.FromText(s);

    /// <summary>True when an optional parameter is written and not omitted; an omitted argument is absent (§5.3, R38).</summary>
    private static bool Given(IFunctionArguments a, int index) => index < a.Count && !a.IsOmitted(index);

    // ---- functions ----

    private static CellValue Len(IFunctionArguments a) =>
        TryText(a, 0, out var s, out var err) ? CellValue.FromNumber(s.Length) : err;

    private static CellValue Map(IFunctionArguments a, Func<string, string> f) =>
        TryText(a, 0, out var s, out var err) ? Text(f(s)) : err;

    private static CellValue Edge(IFunctionArguments a, bool fromLeft)
    {
        if (!TryText(a, 0, out var s, out var err)) return err;
        long n = 1;                                   // §7.3 [n=1]; omitted counts as absent (R38): LEFT(t,) is LEFT(t,1)
        if (Given(a, 1) && !TryInt(a, 1, out n, out err)) return err;
        if (n < 0) return ValueError;
        int take = (int)Math.Min(n, s.Length);
        return Text(fromLeft ? s[..take] : s[^take..]);
    }

    private static CellValue Mid(IFunctionArguments a)
    {
        if (!TryText(a, 0, out var s, out var err)) return err;
        if (!TryInt(a, 1, out var start, out err)) return err;
        if (!TryInt(a, 2, out var n, out err)) return err;
        if (start < 1 || n < 0) return ValueError;
        if (start > s.Length) return EmptyText;
        int from = (int)(start - 1);
        int take = (int)Math.Min(n, s.Length - from);
        return Text(s.Substring(from, take));
    }

    private static string Proper(string s)
    {
        var chars = s.ToCharArray();
        bool prevLetter = false;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            bool letter = char.IsLetter(c);
            if (letter) chars[i] = prevLetter ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);
            prevLetter = letter;
        }
        return new string(chars);
    }

    private static string Trim(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool pendingSpace = false;
        foreach (char c in s)
        {
            if (c == ' ') { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Appends with the length cap; false once the result would exceed it.</summary>
    private static bool TryAppend(StringBuilder sb, string s)
    {
        if (sb.Length + (long)s.Length > Coercion.MaxTextLength) return false;
        sb.Append(s);
        return true;
    }

    private static CellValue Concat(IFunctionArguments a)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < a.Count; i++)
        {
            if (a.IsReference(i))
            {
                foreach (var cell in a.EnumerateReferenceValues(i))
                {
                    var v = cell.Value;
                    if (v.Kind == ValueKind.Error) return v;
                    if (!TryAppend(sb, Coercion.ScalarToText(v))) return OverflowOrFirstError(a, i + 1, cell.Address, i);
                }
            }
            else
            {
                if (!TryText(a, i, out var s, out var err)) return err;
                if (!TryAppend(sb, s)) return OverflowOrFirstError(a, i + 1, null, i);
            }
        }
        return Text(sb.ToString());
    }

    /// <summary>
    /// The result overflowed while scanning; an error later in argument order would still win, so look for one before
    /// reporting #VALUE!. <paramref name="resumeAt"/> is the next argument; the rest of the current range is rescanned.
    /// </summary>
    private static CellValue OverflowOrFirstError(IFunctionArguments a, int resumeAt, Addressing.CellAddress? after, int current)
    {
        if (after is not null)
        {
            bool seen = false;
            foreach (var cell in a.EnumerateReferenceValues(current))
            {
                if (seen && cell.Value.Kind == ValueKind.Error) return cell.Value;
                if (cell.Address == after.Value) seen = true;
            }
        }
        for (int i = resumeAt; i < a.Count; i++)
        {
            if (a.IsReference(i))
            {
                foreach (var cell in a.EnumerateReferenceValues(i))
                    if (cell.Value.Kind == ValueKind.Error) return cell.Value;
            }
            else
            {
                var v = a.Evaluate(i);
                if (v.Kind == ValueKind.Error) return v;
            }
        }
        return ValueError;
    }

    private static CellValue TextJoin(IFunctionArguments a)
    {
        if (!TryText(a, 0, out var delimiter, out var err)) return err;
        var flag = Coercion.ToBoolean(a.Evaluate(1));
        if (flag.Kind == ValueKind.Error) return flag;
        bool ignoreEmpty = flag.Boolean;

        var sb = new StringBuilder();
        bool first = true, overflow = false;
        void Take(string s)
        {
            if (overflow || (ignoreEmpty && s.Length == 0)) return;
            if (!first && !TryAppend(sb, delimiter)) { overflow = true; return; }
            if (!TryAppend(sb, s)) { overflow = true; return; }
            first = false;
        }

        for (int i = 2; i < a.Count; i++)
        {
            if (a.IsReference(i))
            {
                foreach (var cell in a.EnumerateReferenceValues(i))
                {
                    if (cell.Value.Kind == ValueKind.Error) return cell.Value;
                    Take(Coercion.ScalarToText(cell.Value));
                }
            }
            else
            {
                if (!TryText(a, i, out var s, out err)) return err;
                Take(s);
            }
        }
        return overflow ? ValueError : Text(sb.ToString());
    }

    private static CellValue Locate(IFunctionArguments a, bool wildcards)
    {
        if (!TryText(a, 0, out var find, out var err)) return err;
        if (!TryText(a, 1, out var within, out err)) return err;
        long start = 1;                               // §7.3 [s=1]; omitted counts as absent (R38)
        if (Given(a, 2) && !TryInt(a, 2, out start, out err)) return err;
        if (start < 1 || start > within.Length + 1) return ValueError;
        int from = (int)(start - 1);

        int index;
        if (wildcards)
            index = WildcardSearch(find.ToUpperInvariant(), within.ToUpperInvariant(), from);
        else
            index = within.IndexOf(find, from, StringComparison.Ordinal);
        return index < 0 ? ValueError : CellValue.FromNumber(index + 1);
    }

    private enum Kind : byte { Literal, AnyOne, Star }

    private readonly record struct Token(Kind Kind, char Char);

    /// <summary>Zero-based index of the first match of <paramref name="pattern"/> at or after <paramref name="from"/>, or -1.</summary>
    private static int WildcardSearch(string pattern, string text, int from)
    {
        // Tokenise (§7.3): ~ escapes the next character, whatever it is; a trailing lone ~ stands for itself.
        var tokens = new List<Token>(pattern.Length);
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '~' && i + 1 < pattern.Length)
                tokens.Add(new Token(Kind.Literal, pattern[++i]));
            else if (c == '*') tokens.Add(new Token(Kind.Star, c));
            else if (c == '?') tokens.Add(new Token(Kind.AnyOne, c));
            else tokens.Add(new Token(Kind.Literal, c));
        }

        // Split on stars into non-empty segments.
        var segments = new List<Token[]>();
        var current = new List<Token>();
        bool leadingStar = tokens.Count > 0 && tokens[0].Kind == Kind.Star;
        foreach (var t in tokens)
        {
            if (t.Kind == Kind.Star)
            {
                if (current.Count > 0) { segments.Add([.. current]); current.Clear(); }
            }
            else current.Add(t);
        }
        if (current.Count > 0) segments.Add([.. current]);

        if (segments.Count == 0) return from;           // "" or only stars: matches at the start position

        int pos = from;
        int firstIndex = from;
        for (int s = 0; s < segments.Count; s++)
        {
            int at = FindSegment(segments[s], text, pos);
            if (at < 0) return -1;                       // leftmost placement failed, so every later one fails too
            if (s == 0) firstIndex = at;
            pos = at + segments[s].Length;
        }
        return leadingStar ? from : firstIndex;
    }

    private static int FindSegment(Token[] segment, string text, int from)
    {
        int last = text.Length - segment.Length;
        for (int p = from; p <= last; p++)
        {
            int k = 0;
            while (k < segment.Length && (segment[k].Kind == Kind.AnyOne || segment[k].Char == text[p + k])) k++;
            if (k == segment.Length) return p;
        }
        return -1;
    }

    private static CellValue Substitute(IFunctionArguments a)
    {
        if (!TryText(a, 0, out var text, out var err)) return err;
        if (!TryText(a, 1, out var oldText, out err)) return err;
        if (!TryText(a, 2, out var newText, out err)) return err;
        long instance = 0;                            // absent (or omitted, R38): every occurrence
        if (Given(a, 3))
        {
            if (!TryInt(a, 3, out instance, out err)) return err;
            if (instance < 1) return ValueError;
        }
        if (oldText.Length == 0) return Text(text);

        if (instance > 0)
        {
            int idx = -1, searchFrom = 0;
            for (long k = 0; k < instance; k++)
            {
                idx = text.IndexOf(oldText, searchFrom, StringComparison.Ordinal);
                if (idx < 0) return Text(text);
                searchFrom = idx + oldText.Length;
            }
            long len = (long)text.Length - oldText.Length + newText.Length;
            if (len > Coercion.MaxTextLength) return ValueError;
            return Text(string.Concat(text.AsSpan(0, idx), newText, text.AsSpan(idx + oldText.Length)));
        }

        // Replace all: count non-overlapping matches first so the length is known before building.
        long count = 0;
        for (int i = text.IndexOf(oldText, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(oldText, i + oldText.Length, StringComparison.Ordinal))
            count++;
        if (count == 0) return Text(text);
        long total = text.Length + count * ((long)newText.Length - oldText.Length);
        if (total > Coercion.MaxTextLength) return ValueError;
        return Text(text.Replace(oldText, newText, StringComparison.Ordinal));
    }

    private static CellValue Replace(IFunctionArguments a)
    {
        if (!TryText(a, 0, out var text, out var err)) return err;
        if (!TryInt(a, 1, out var start, out err)) return err;
        if (!TryInt(a, 2, out var n, out err)) return err;
        if (!TryText(a, 3, out var newText, out err)) return err;
        if (start < 1 || n < 0) return ValueError;
        int from = (int)Math.Min(start - 1, text.Length);
        int remove = (int)Math.Min(n, text.Length - from);
        long len = (long)text.Length - remove + newText.Length;
        if (len > Coercion.MaxTextLength) return ValueError;
        return Text(string.Concat(text.AsSpan(0, from), newText, text.AsSpan(from + remove)));
    }

    private static CellValue Rept(IFunctionArguments a)
    {
        if (!TryText(a, 0, out var text, out var err)) return err;
        if (!Coercion.TryToNumber(a.Evaluate(1), out var raw, out err)) return err;
        double n = Math.Truncate(raw);
        if (n < 0) return ValueError;
        if (text.Length == 0 || n == 0) return EmptyText;
        if (n * text.Length > Coercion.MaxTextLength) return ValueError;
        return Text(new StringBuilder(text.Length * (int)n).Insert(0, text, (int)n).ToString());
    }

    private static CellValue Exact(IFunctionArguments a)
    {
        if (!TryText(a, 0, out var x, out var err)) return err;
        if (!TryText(a, 1, out var y, out err)) return err;
        return CellValue.FromBoolean(string.Equals(x, y, StringComparison.Ordinal));
    }

    private static CellValue Value(IFunctionArguments a) => Coercion.ToNumber(a.Evaluate(0));
}
