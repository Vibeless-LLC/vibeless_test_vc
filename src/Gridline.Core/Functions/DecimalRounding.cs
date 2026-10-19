using System.Globalization;

namespace Gridline.Core.Functions;

/// <summary>Rounding direction for <see cref="DecimalRounding"/>.</summary>
internal enum RoundMode
{
    /// <summary>Ties (exactly half) go away from zero.</summary>
    HalfAwayFromZero,
    /// <summary>Any discarded non-zero digit moves away from zero.</summary>
    AwayFromZero,
    /// <summary>Discarded digits are dropped (toward zero).</summary>
    TowardZero,
}

/// <summary>
/// Rounding on the shortest round-trip decimal form of a double (§7.1), so ROUND(2.675,2) is 2.68 even though the
/// binary value is 2.67499999999999982236431605997495353221893310546875.
/// </summary>
/// <remarks>
/// The double is formatted with "R" (at most 17 significant digits), split into an integer mantissa and a power of ten,
/// and rounded with integer arithmetic. No decimal type is used, so there is no magnitude limit: digits beyond the
/// mantissa are handled directly (a value far below the rounding unit becomes 0, or one unit for AwayFromZero), and a
/// value already coarser than the requested unit is returned unchanged. The result is rebuilt by parsing
/// "mantissa E exponent", which is correctly rounded. A result that does not fit a double is infinite, which
/// <see cref="CellValue.FromNumber"/> turns into #NUM!.
/// </summary>
internal static class DecimalRounding
{
    /// <summary>Digit counts beyond this behave identically to it for any double (smallest is 5E-324, largest 1.8E+308).</summary>
    private const int MaxDigits = 1000;

    /// <summary>Rounds <paramref name="value"/> to <paramref name="digits"/> decimal places (negative: tens, hundreds, ...).</summary>
    public static double Round(double value, int digits, RoundMode mode)
    {
        if (value == 0) return 0;
        digits = Math.Clamp(digits, -MaxDigits, MaxDigits);

        Decompose(value, out bool negative, out long mantissa, out int exponent); // |value| = mantissa * 10^exponent
        int shift = -digits - exponent; // number of low decimal digits of the mantissa to discard
        if (shift <= 0) return value;   // already a multiple of the unit

        long quotient;
        if (shift > 18)
        {
            // mantissa < 10^18 <= half unit: everything is discarded and it is below a tie
            quotient = mode == RoundMode.AwayFromZero ? 1 : 0;
        }
        else
        {
            long divisor = Pow10(shift);
            quotient = Math.DivRem(mantissa, divisor, out long remainder);
            bool bump = mode switch
            {
                RoundMode.HalfAwayFromZero => remainder * 2 >= divisor,
                RoundMode.AwayFromZero => remainder > 0,
                _ => false,
            };
            if (bump) quotient++;
        }

        if (quotient == 0) return 0;
        var text = string.Create(CultureInfo.InvariantCulture, $"{(negative ? "-" : "")}{quotient}E{-digits}");
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Splits the shortest round-trip form into sign, integer mantissa and decimal exponent.</summary>
    private static void Decompose(double value, out bool negative, out long mantissa, out int exponent)
    {
        string r = value.ToString("R", CultureInfo.InvariantCulture); // e.g. "-2.675", "1E-05", "1.5E+300"
        var span = r.AsSpan();
        negative = span[0] == '-';
        if (negative) span = span[1..];

        int exp = 0;
        int e = span.IndexOfAny('E', 'e');
        if (e >= 0)
        {
            exp = int.Parse(span[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            span = span[..e];
        }

        long m = 0;
        int fractionDigits = 0;
        bool afterPoint = false;
        foreach (char c in span)
        {
            if (c == '.') { afterPoint = true; continue; }
            m = m * 10 + (c - '0'); // at most 17 significant digits, fits in long
            if (afterPoint) fractionDigits++;
        }
        mantissa = m;
        exponent = exp - fractionDigits;
    }

    private static long Pow10(int n)
    {
        long p = 1;
        for (int i = 0; i < n; i++) p *= 10;
        return p;
    }
}
