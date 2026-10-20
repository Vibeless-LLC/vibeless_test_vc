using Gridline.Core.Evaluation;

namespace Gridline.Core.Functions;

/// <summary>
/// The 20 math functions of §7.1 and the §7.0 aggregation rules. Registered by <see cref="Register"/>.
/// </summary>
/// <remarks>
/// Aggregation (§7.0), used by SUM, AVERAGE, MIN, MAX, PRODUCT and SUMSQ: a reference argument contributes only its
/// numeric cells (text, booleans and blanks are ignored) and an error cell propagates; a direct argument is coerced by
/// <see cref="Coercion.TryToNumber"/> (number as is, boolean 1/0, numeric text parsed, blank 0), failure is #VALUE! and
/// an error propagates. An omitted argument has no default and is #VALUE! in its place (§5.3, R38); only COUNT and
/// COUNTA, which never error (§7.1), skip it. The first error in argument order (row-major inside a range) wins.
/// Integer truncation (§6.9): only the ROUND family has an integer parameter (digits); it truncates toward zero
/// (2.9 becomes 2, -2.9 becomes -2). INT is floor (not truncation), and no other function takes an integer argument.
/// </remarks>
internal static class MathFunctions
{
    private static readonly CellValue ValueError = CellValue.FromError(ErrorCodes.Value);
    private static readonly CellValue NumError = CellValue.FromError(ErrorCodes.Num);
    private static readonly CellValue Div0Error = CellValue.FromError(ErrorCodes.Div0);

    public static void Register(BuiltInFunctionTable.Builder b)
    {
        const int Max = BuiltInFunctionTable.MaxVariadicArguments;
        Add(b, "SUM", 1, Max, a => Aggregate(a, Kind.Sum));
        Add(b, "AVERAGE", 1, Max, a => Aggregate(a, Kind.Average));
        Add(b, "MIN", 1, Max, a => Aggregate(a, Kind.Min));
        Add(b, "MAX", 1, Max, a => Aggregate(a, Kind.Max));
        Add(b, "PRODUCT", 1, Max, a => Aggregate(a, Kind.Product));
        Add(b, "SUMSQ", 1, Max, a => Aggregate(a, Kind.SumSq));
        Add(b, "COUNT", 1, Max, Count);
        Add(b, "COUNTA", 1, Max, CountA);
        Add(b, "COUNTBLANK", 1, 1, CountBlank);
        Add(b, "SUMPRODUCT", 1, Max, SumProduct);
        Add(b, "ABS", 1, 1, a => Unary(a, Math.Abs));
        Add(b, "SIGN", 1, 1, a => Unary(a, x => Math.Sign(x)));
        Add(b, "INT", 1, 1, a => Unary(a, Math.Floor));
        Add(b, "SQRT", 1, 1, Sqrt);
        Add(b, "MOD", 2, 2, Mod);
        Add(b, "POWER", 2, 2, Power);
        Add(b, "PI", 0, 0, _ => CellValue.FromNumber(Math.PI));
        Add(b, "ROUND", 2, 2, a => Round(a, RoundMode.HalfAwayFromZero));
        Add(b, "ROUNDUP", 2, 2, a => Round(a, RoundMode.AwayFromZero));
        Add(b, "ROUNDDOWN", 2, 2, a => Round(a, RoundMode.TowardZero));
    }

    private static void Add(BuiltInFunctionTable.Builder b, string name, int min, int max, Func<IFunctionArguments, CellValue> body) =>
        b.Add(name, min, max, new DelegateFunction(body));

    // ---- Aggregation ----

    private enum Kind { Sum, Average, Min, Max, Product, SumSq }

    /// <summary>Running totals; a struct so that per-cell accumulation allocates nothing.</summary>
    private struct Totals
    {
        public long Count;
        public double Sum, SumSq, Product, Min, Max;

        public void Add(double x)
        {
            if (Count == 0) { Min = Max = x; Product = x; }
            else
            {
                if (x < Min) Min = x;
                if (x > Max) Max = x;
                Product *= x;
            }
            Count++;
            Sum += x;
            SumSq += x * x;
        }
    }

    private static CellValue Aggregate(IFunctionArguments a, Kind kind)
    {
        var totals = new Totals();
        for (int i = 0; i < a.Count; i++)
        {
            if (a.IsReference(i))
            {
                foreach (var cell in a.EnumerateReferenceValues(i))
                {
                    var v = cell.Value;
                    if (v.Kind == ValueKind.Number) totals.Add(v.Number);
                    else if (v.Kind == ValueKind.Error) return v;
                }
            }
            else
            {
                if (!Coercion.TryToNumber(a.Evaluate(i), out double n, out var error)) return error;
                totals.Add(n);
            }
        }

        return kind switch
        {
            Kind.Sum => CellValue.FromNumber(totals.Sum),
            Kind.SumSq => CellValue.FromNumber(totals.SumSq),
            Kind.Average => totals.Count == 0 ? Div0Error : CellValue.FromNumber(totals.Sum / totals.Count),
            Kind.Min => CellValue.FromNumber(totals.Count == 0 ? 0 : totals.Min),         // R17
            Kind.Max => CellValue.FromNumber(totals.Count == 0 ? 0 : totals.Max),         // R17
            _ => CellValue.FromNumber(totals.Count == 0 ? 0 : totals.Product),            // R17
        };
    }

    /// <summary>COUNT (§7.1, R17, R38): references count numbers; direct arguments count numbers, booleans and numeric text. Omitted arguments are ignored and errors are never propagated.</summary>
    private static CellValue Count(IFunctionArguments a)
    {
        long count = 0;
        for (int i = 0; i < a.Count; i++)
        {
            if (a.IsReference(i))
            {
                foreach (var cell in a.EnumerateReferenceValues(i))
                    if (cell.Value.Kind == ValueKind.Number) count++;
            }
            else if (!a.IsOmitted(i))                     // R38: an omitted argument is ignored (COUNT(1,,2) = 2)
            {
                var v = a.Evaluate(i);
                if (v.Kind is ValueKind.Number or ValueKind.Boolean) count++;
                else if (v.Kind == ValueKind.Text && Coercion.TryToNumber(v, out _, out _)) count++;
            }
        }
        return CellValue.FromNumber(count);
    }

    /// <summary>COUNTA (§7.1, R17, R38): every non-blank value, including errors and "" results. An omitted argument is ignored (COUNTA(1,,2) = 2).</summary>
    private static CellValue CountA(IFunctionArguments a)
    {
        long count = 0;
        for (int i = 0; i < a.Count; i++)
        {
            if (a.IsReference(i))
            {
                foreach (var cell in a.EnumerateReferenceValues(i))
                    if (!cell.IsBlank) count++;
            }
            else if (!a.IsOmitted(i) && a.Evaluate(i).Kind != ValueKind.Blank)
            {
                count++;
            }
        }
        return CellValue.FromNumber(count);
    }

    /// <summary>COUNTBLANK (R17): blank cells and "" text results; the argument must be a reference, else #VALUE!.</summary>
    private static CellValue CountBlank(IFunctionArguments a)
    {
        if (!a.IsReference(0)) return ValueError;
        long count = 0;
        foreach (var cell in a.EnumerateReferenceValues(0))
        {
            var v = cell.Value;
            if (v.Kind == ValueKind.Blank || (v.Kind == ValueKind.Text && v.Text.Length == 0)) count++;
        }
        return CellValue.FromNumber(count);
    }

    /// <summary>
    /// SUMPRODUCT (R17): arguments are multiplied element by element and the products summed. Reference arguments must
    /// have the same width and height, else #VALUE!; a direct argument is a 1x1 array. Non-numeric entries count as 0
    /// and errors propagate: the first error in argument order wins (R35), argument 1 scanned fully before argument 2.
    /// </summary>
    private static CellValue SumProduct(IFunctionArguments a)
    {
        int n = a.Count;
        var enumerators = new ReferenceValues.Enumerator[n];
        var scalars = new double[n];
        long width = 1, height = 1;
        bool haveShape = false;
        for (int i = 0; i < n; i++)
        {
            long w = 1, h = 1;
            if (a.IsReference(i))
            {
                var refs = a.EnumerateReferenceValues(i);
                var range = refs.Range;
                w = range.BottomRight.Column - range.TopLeft.Column + 1;
                h = range.BottomRight.Row - range.TopLeft.Row + 1;
                enumerators[i] = refs.GetEnumerator();
            }
            if (!haveShape) { width = w; height = h; haveShape = true; }
            else if (w != width || h != height) return ValueError;
        }

        // R35: error scan in argument order; each reference argument is scanned in full before the next argument.
        for (int i = 0; i < n; i++)
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
                scalars[i] = v.Kind == ValueKind.Number ? v.Number : 0;
            }
        }

        double total = 0;
        for (long k = width * height; k > 0; k--)
        {
            double product = 1;
            for (int i = 0; i < n; i++)
            {
                if (a.IsReference(i))
                {
                    enumerators[i].MoveNext();
                    var v = enumerators[i].Current.Value;
                    product *= v.Kind == ValueKind.Number ? v.Number : 0;
                }
                else
                {
                    product *= scalars[i];
                }
            }
            total += product;
        }
        return CellValue.FromNumber(total);
    }

    // ---- Scalar functions ----

    private static CellValue Unary(IFunctionArguments a, Func<double, double> f) =>
        Coercion.TryToNumber(a.Evaluate(0), out double x, out var error) ? CellValue.FromNumber(f(x)) : error;

    private static CellValue Sqrt(IFunctionArguments a)
    {
        if (!Coercion.TryToNumber(a.Evaluate(0), out double x, out var error)) return error;
        return x < 0 ? NumError : CellValue.FromNumber(Math.Sqrt(x));
    }

    private const double ModQuotientLimit = 134_217_728; // 2^27

    /// <summary>
    /// MOD(n,d) (§7.1, R35): the result has the sign of d, i.e. n - d*floor(n/d), computed with exact IEEE fmod and then
    /// adjusted to d's sign. d = 0 is #DIV/0!; |n/d| >= 2^27 is #NUM! (Excel's documented limit).
    /// </summary>
    private static CellValue Mod(IFunctionArguments a)
    {
        if (!TryTwoNumbers(a, out double n, out double d, out var error)) return error;
        if (d == 0) return Div0Error;
        if (!(Math.Abs(n / d) < ModQuotientLimit)) return NumError;
        double r = n % d; // exact fmod, same sign as n
        if (r != 0 && (r < 0) != (d < 0)) r += d;
        return CellValue.FromNumber(r);
    }

    /// <summary>POWER(x,y) behaves like the ^ operator (§6.3): 0^0 #NUM!, 0^negative #DIV/0!, negative base with non-integer exponent #NUM!.</summary>
    private static CellValue Power(IFunctionArguments a)
    {
        if (!TryTwoNumbers(a, out double x, out double y, out var error)) return error;
        if (x == 0)
        {
            if (y == 0) return NumError;
            if (y < 0) return Div0Error;
        }
        if (x < 0 && y != Math.Floor(y)) return NumError;
        return CellValue.FromNumber(Math.Pow(x, y));
    }

    /// <summary>Both arguments are evaluated and coerced left to right; the first failure wins (§6.4).</summary>
    private static bool TryTwoNumbers(IFunctionArguments a, out double first, out double second, out CellValue error)
    {
        second = 0;
        if (!Coercion.TryToNumber(a.Evaluate(0), out first, out error)) return false;
        return Coercion.TryToNumber(a.Evaluate(1), out second, out error);
    }

    /// <summary>
    /// ROUND, ROUNDUP, ROUNDDOWN (§7.1, §6.9). The digits argument truncates toward zero (2.9 becomes 2, -2.9 becomes -2);
    /// a negative count rounds to tens, hundreds and so on. Rounding works on the shortest round-trip decimal text of the
    /// number (<see cref="DecimalRounding"/>); a result beyond double range is #NUM!.
    /// </summary>
    private static CellValue Round(IFunctionArguments a, RoundMode mode)
    {
        if (!TryTwoNumbers(a, out double x, out double digits, out var error)) return error;
        int d = (int)Math.Clamp(Math.Truncate(digits), -100_000, 100_000);
        return CellValue.FromNumber(DecimalRounding.Round(x, d, mode));
    }
}
