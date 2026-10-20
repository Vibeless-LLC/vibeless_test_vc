namespace Gridline.Core.Functions;

/// <summary>
/// The wildcard matcher shared by SEARCH (§7.3) and the exact-mode lookups (§7.4, R66). In a pattern <c>*</c> matches any
/// run of characters, <c>?</c> exactly one, and <c>~</c> escapes the next character whatever it is (D-32); a trailing lone
/// <c>~</c> stands for itself. Callers upper-case both strings for case-insensitive matching. No recursion: O(n * m).
/// </summary>
internal static class WildcardMatcher
{
    private enum Kind : byte { Literal, AnyOne, Star }

    private readonly record struct Token(Kind Kind, char Char);

    private static List<Token> Tokenise(string pattern)
    {
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
        return tokens;
    }

    /// <summary>True when <paramref name="pattern"/> matches the whole of <paramref name="text"/>.</summary>
    public static bool IsMatch(string pattern, string text)
    {
        var tokens = Tokenise(pattern);
        int p = 0, t = 0, starAt = -1, starText = 0;
        while (t < text.Length)
        {
            if (p < tokens.Count && tokens[p].Kind == Kind.Star) { starAt = p++; starText = t; }
            else if (p < tokens.Count && (tokens[p].Kind == Kind.AnyOne || tokens[p].Char == text[t])) { p++; t++; }
            else if (starAt >= 0) { p = starAt + 1; t = ++starText; }
            else return false;
        }
        while (p < tokens.Count && tokens[p].Kind == Kind.Star) p++;
        return p == tokens.Count;
    }

    /// <summary>Zero-based index of the first match of <paramref name="pattern"/> at or after <paramref name="from"/>, or -1.</summary>
    public static int Search(string pattern, string text, int from)
    {
        var tokens = Tokenise(pattern);

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
}
