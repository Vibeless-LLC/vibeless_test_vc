using Gridline.Core.Evaluation;

namespace Gridline.Core.Functions;

/// <summary>
/// The logic and information functions of §7.2 (rulings R16, R18). Registered by <see cref="Register"/>.
/// </summary>
/// <remarks>
/// IF evaluates its condition and then only the chosen branch; IFERROR/IFNA evaluate the alternative only when needed.
/// Omitted arguments: <c>IF(c,)</c> returns 0 for the true branch, <c>IF(c,t)</c> (no third argument) returns FALSE, and
/// <c>IF(c,t,)</c> (written but empty third argument) returns 0, as Excel does.
/// AND/OR/XOR: a reference argument contributes its boolean cells and its numbers (non-zero is TRUE) and ignores text
/// and blanks; a direct argument is coerced by <see cref="Coercion.ToBoolean"/> (omitted counts as FALSE). An error
/// propagates (first in argument order). With no logical value at all the result is #VALUE! (R18).
/// The IS* tests never return an error; a multi-cell range gives FALSE (R18). ISBLANK is TRUE only for a reference to a
/// single truly blank cell: a formula result of "" is text, and a non-reference argument gives FALSE.
/// </remarks>
internal static class LogicFunctions
{
    private static readonly CellValue ValueError = CellValue.FromError(ErrorCodes.Value);
    private static readonly CellValue True = CellValue.FromBoolean(true);
    private static readonly CellValue False = CellValue.FromBoolean(false);
    private static readonly CellValue Zero = CellValue.FromNumber(0);

    public static void Register(BuiltInFunctionTable.Builder b)
    {
        const int Max = BuiltInFunctionTable.MaxVariadicArguments;
        Add(b, "IF", 2, 3, If);
        Add(b, "AND", 1, Max, a => Combine(a, Op.And));
        Add(b, "OR", 1, Max, a => Combine(a, Op.Or));
        Add(b, "XOR", 1, Max, a => Combine(a, Op.Xor));
        Add(b, "NOT", 1, 1, Not);
        Add(b, "IFERROR", 2, 2, a => IfError(a, naOnly: false));
        Add(b, "IFNA", 2, 2, a => IfError(a, naOnly: true));
        Add(b, "ISBLANK", 1, 1, IsBlank);
        Add(b, "ISNUMBER", 1, 1, a => Test(a, v => v.Kind == ValueKind.Number));
        Add(b, "ISTEXT", 1, 1, a => Test(a, v => v.Kind == ValueKind.Text));
        Add(b, "ISLOGICAL", 1, 1, a => Test(a, v => v.Kind == ValueKind.Boolean));
        Add(b, "ISERROR", 1, 1, a => Test(a, v => v.Kind == ValueKind.Error));
        Add(b, "ISNA", 1, 1, a => Test(a, v => v.Kind == ValueKind.Error && v.Error == ErrorCodes.NA));
    }

    private static void Add(BuiltInFunctionTable.Builder b, string name, int min, int max, Func<IFunctionArguments, CellValue> body) =>
        b.Add(name, min, max, new DelegateFunction(body));

    private static CellValue If(IFunctionArguments a)
    {
        var condition = Coercion.ToBoolean(a.Evaluate(0));
        if (condition.Kind == ValueKind.Error) return condition;
        int branch = condition.Boolean ? 1 : 2;
        if (branch >= a.Count) return False;            // IF(c,t) with no third argument
        if (a.IsOmitted(branch)) return Zero;            // IF(c,,f) and IF(c,t,)
        return a.Evaluate(branch);
    }

    private enum Op { And, Or, Xor }

    private static CellValue Combine(IFunctionArguments a, Op op)
    {
        long logical = 0, trues = 0;
        for (int i = 0; i < a.Count; i++)
        {
            if (a.IsReference(i))
            {
                foreach (var cell in a.EnumerateReferenceValues(i))
                {
                    var v = cell.Value;
                    if (v.Kind == ValueKind.Boolean) { logical++; if (v.Boolean) trues++; }
                    else if (v.Kind == ValueKind.Number) { logical++; if (v.Number != 0) trues++; }
                    else if (v.Kind == ValueKind.Error) return v;
                }
            }
            else
            {
                var v = Coercion.ToBoolean(a.Evaluate(i));
                if (v.Kind == ValueKind.Error) return v;
                logical++;
                if (v.Boolean) trues++;
            }
        }

        if (logical == 0) return ValueError;             // R18
        return CellValue.FromBoolean(op switch
        {
            Op.And => trues == logical,
            Op.Or => trues > 0,
            _ => (trues & 1) == 1,
        });
    }

    private static CellValue Not(IFunctionArguments a)
    {
        var v = Coercion.ToBoolean(a.Evaluate(0));
        return v.Kind == ValueKind.Error ? v : CellValue.FromBoolean(!v.Boolean);
    }

    private static CellValue IfError(IFunctionArguments a, bool naOnly)
    {
        var v = a.Evaluate(0);
        if (v.Kind != ValueKind.Error || (naOnly && v.Error != ErrorCodes.NA)) return v;
        return a.Evaluate(1);
    }

    private static CellValue IsBlank(IFunctionArguments a)
    {
        if (!a.IsReference(0)) return False;
        var cells = a.EnumerateReferenceValues(0);
        if (cells.Count != 1) return False;
        foreach (var cell in cells) return CellValue.FromBoolean(cell.IsBlank);
        return False;
    }

    /// <summary>A type test: never an error, FALSE for a multi-cell range (R18).</summary>
    private static CellValue Test(IFunctionArguments a, Func<CellValue, bool> predicate)
    {
        if (a.IsReference(0) && a.EnumerateReferenceValues(0).Count != 1) return False;
        return CellValue.FromBoolean(predicate(a.Evaluate(0)));
    }
}
