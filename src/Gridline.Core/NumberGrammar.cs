using System.Globalization;

namespace Gridline.Core;

/// <summary>
/// The number grammar of §4.3: <c>^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?%?$</c>, with \d read as ASCII 0-9.
/// Also reused for text-to-number coercion (§6.2). Parsing is culture-invariant; a trailing % divides by 100;
/// a non-finite result is not a number.
/// </summary>
public static class NumberGrammar
{
    public static bool TryParse(ReadOnlySpan<char> s, out double value)
    {
        value = 0;
        if (!Matches(s, out var percent)) return false;
        var body = percent ? s[..^1] : s;
        if (!double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return false;
        if (percent) d /= 100;
        if (!double.IsFinite(d)) return false;
        value = d == 0 ? 0.0 : d; // normalise -0
        return true;
    }

    private static bool Matches(ReadOnlySpan<char> s, out bool percent)
    {
        percent = false;
        int i = 0, n = s.Length;
        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
        int intDigits = Digits(s, ref i);
        if (intDigits > 0)
        {
            if (i < n && s[i] == '.') { i++; Digits(s, ref i); }
        }
        else
        {
            if (i >= n || s[i] != '.') return false;
            i++;
            if (Digits(s, ref i) == 0) return false;
        }
        if (i < n && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < n && (s[i] == '+' || s[i] == '-')) i++;
            if (Digits(s, ref i) == 0) return false;
        }
        if (i < n && s[i] == '%') { percent = true; i++; }
        return i == n;
    }

    private static int Digits(ReadOnlySpan<char> s, ref int i)
    {
        int start = i;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
        return i - start;
    }
}
