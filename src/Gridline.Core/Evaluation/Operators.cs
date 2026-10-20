using Gridline.Core.Parsing;

namespace Gridline.Core.Evaluation;

/// <summary>
/// The §6.3 operators on already evaluated operands. Errors propagate left first (§6.4); for arithmetic the left
/// operand is fully coerced before the right is looked at, so <c>"a"+#N/A</c> is #VALUE!. Numeric results go through
/// <see cref="CellValue.FromNumber"/> (non-finite becomes #NUM!, -0 becomes 0).
/// </summary>
internal static class Operators
{
    public static CellValue Binary(BinaryOperator op, CellValue left, CellValue right)
    {
        if (left.Kind == ValueKind.Error) return left;
        switch (op)
        {
            case BinaryOperator.Concat:
                if (right.Kind == ValueKind.Error) return right;
                return Concat(left, right);
            case BinaryOperator.Equal:
            case BinaryOperator.NotEqual:
            case BinaryOperator.Less:
            case BinaryOperator.Greater:
            case BinaryOperator.LessEqual:
            case BinaryOperator.GreaterEqual:
                if (right.Kind == ValueKind.Error) return right;
                return CellValue.FromBoolean(Compare(op, ValueComparer.Compare(left, right)));
        }

        if (!Coercion.TryToNumber(left, out var a, out var error)) return error;
        if (!Coercion.TryToNumber(right, out var b, out error)) return error;
        return op switch
        {
            BinaryOperator.Add => CellValue.FromNumber(a + b),
            BinaryOperator.Subtract => CellValue.FromNumber(a - b),
            BinaryOperator.Multiply => CellValue.FromNumber(a * b),
            BinaryOperator.Divide => b == 0 ? Errors.Div0 : CellValue.FromNumber(a / b),
            BinaryOperator.Power => Power(a, b),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };
    }

    /// <summary>
    /// Negation coerces to a number. Unary + is a no-op returning its operand unchanged (ruling R15: <c>+"x"</c> is
    /// "x", <c>+TRUE</c> is TRUE), except that a blank operand yields 0 (§6.6).
    /// </summary>
    public static CellValue Unary(UnaryOperator op, CellValue operand)
    {
        if (op == UnaryOperator.Plus)
            return operand.Kind == ValueKind.Blank ? CellValue.FromNumber(0) : operand;
        if (!Coercion.TryToNumber(operand, out var a, out var error)) return error;
        return CellValue.FromNumber(-a);
    }

    public static CellValue Percent(CellValue operand)
    {
        if (!Coercion.TryToNumber(operand, out var a, out var error)) return error;
        return CellValue.FromNumber(a / 100);
    }

    /// <summary>§6.3 power: 0^0 #NUM!, 0^negative #DIV/0!, negative base with non-integer exponent #NUM!.</summary>
    private static CellValue Power(double a, double b)
    {
        if (a == 0)
        {
            if (b == 0) return Errors.Num;
            if (b < 0) return Errors.Div0;
        }
        if (a < 0 && b != Math.Floor(b)) return Errors.Num;
        return CellValue.FromNumber(Math.Pow(a, b));
    }

    private static CellValue Concat(CellValue left, CellValue right)
    {
        var l = Coercion.ScalarToText(left);
        var r = Coercion.ScalarToText(right);
        if ((long)l.Length + r.Length > Coercion.MaxTextLength) return Errors.Value;
        return CellValue.FromText(string.Concat(l, r));
    }

    private static bool Compare(BinaryOperator op, int c) => op switch
    {
        BinaryOperator.Equal => c == 0,
        BinaryOperator.NotEqual => c != 0,
        BinaryOperator.Less => c < 0,
        BinaryOperator.Greater => c > 0,
        BinaryOperator.LessEqual => c <= 0,
        _ => c >= 0,
    };
}
