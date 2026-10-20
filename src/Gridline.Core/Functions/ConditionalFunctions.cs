using Gridline.Core.Addressing;
using Gridline.Core.Evaluation;

namespace Gridline.Core.Functions;

/// <summary>
/// The conditional aggregation functions of §7.5 (ruling R68, D-45): SUMIF, COUNTIF, AVERAGEIF. Registered by <see cref="Register"/>.
/// </summary>
/// <remarks>
/// <c>range</c>, <c>sum_range</c> and <c>avg_range</c> are reference-required positions (D-41): a name that cannot be a
/// reference gives #NAME?/#REF! first, any other non-reference is #VALUE! (R68). <c>criteria</c> is an ordinary value.
/// Arguments are processed in order (§6.4). The criterion is parsed once into a <see cref="Criterion"/>, then each cell
/// of <c>range</c> is read lazily; <c>sum_range</c> is read at the same offset from its own top-left cell, and positions
/// that fall beyond the grid are ignored. The static dependency on the resized rectangle is the extractor's job
/// (Dependencies), not this file's.
/// </remarks>
internal static class ConditionalFunctions
{
    private static readonly CellValue ValueError = CellValue.FromError(ErrorCodes.Value);
    private static readonly CellValue Div0Error = CellValue.FromError(ErrorCodes.Div0);

    public static void Register(BuiltInFunctionTable.Builder b)
    {
        b.Add("SUMIF", 2, 3, new DelegateFunction(a => Aggregate(a, Mode.Sum)));
        b.Add("COUNTIF", 2, 2, new DelegateFunction(a => Aggregate(a, Mode.Count)));
        b.Add("AVERAGEIF", 2, 3, new DelegateFunction(a => Aggregate(a, Mode.Average)));
    }

    private enum Mode { Sum, Count, Average }

    private static bool RequireReference(IFunctionArguments a, int index, out CellValue error)
    {
        if (a.TryGetReferenceError(index, out error)) return false;      // D-41: #NAME? / #REF! before #VALUE!
        if (a.IsReference(index)) return true;
        error = ValueError;                                              // R68: a direct, non-reference argument
        return false;
    }

    private static CellValue Aggregate(IFunctionArguments a, Mode mode)
    {
        if (!RequireReference(a, 0, out var err)) return err;
        var range = a.EnumerateReferenceValues(0);

        var criteriaValue = a.Evaluate(1);
        if (criteriaValue.Kind == ValueKind.Error) return criteriaValue;   // R68: an error criterion propagates
        var criterion = Criterion.Parse(criteriaValue);

        var values = range;                                                // where numbers are taken from
        bool hasValues = mode != Mode.Count && a.Count > 2 && !a.IsOmitted(2);
        if (hasValues)
        {
            if (!RequireReference(a, 2, out err)) return err;
            values = a.EnumerateReferenceValues(2);
        }

        int rows = range.Rows, columns = range.Columns;
        var anchor = values.Range.TopLeft;
        double sum = 0;
        long matched = 0, numbers = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                if (!criterion.Matches(range.ValueAt(r, c))) continue;
                matched++;
                if (mode == Mode.Count) continue;
                if (anchor.Row + r > GridBounds.MaxRows || anchor.Column + c > GridBounds.MaxColumns) continue; // beyond the grid
                var v = values.ValueAt(r, c);
                if (v.Kind == ValueKind.Error) return v;                   // a matched position's error propagates
                if (v.Kind != ValueKind.Number) continue;                  // only numbers are aggregated
                sum += v.Number;
                numbers++;
            }
        }

        return mode switch
        {
            Mode.Count => CellValue.FromNumber(matched),
            Mode.Sum => CellValue.FromNumber(sum),
            _ => numbers == 0 ? Div0Error : CellValue.FromNumber(sum / numbers),
        };
    }

    private enum Op { Eq, Ne, Lt, Gt, Le, Ge }

    private enum Shape : byte { Number, Boolean, Text, Empty, NotEmpty }

    /// <summary>A parsed criterion (§7.5): what a cell of <c>range</c> must be to match. Error cells never match.</summary>
    private sealed class Criterion
    {
        private Shape _shape;
        private Op _op;
        private double _number;
        private bool _boolean;
        private CellValue _text;                      // the operand as a text value, for ordering
        private WildcardMatcher.Compiled? _pattern;   // upper-cased operand, for = and <>

        public static Criterion Parse(CellValue criteria)
        {
            var c = new Criterion { _op = Op.Eq };
            switch (criteria.Kind)
            {
                case ValueKind.Number: c._shape = Shape.Number; c._number = criteria.Number; return c;   // a value means equality
                case ValueKind.Boolean: c._shape = Shape.Boolean; c._boolean = criteria.Boolean; return c;
                case ValueKind.Blank: c._shape = Shape.Empty; return c;                                  // R68: blank acts as ""
            }

            var s = criteria.Text;
            int len = 0;
            if (s.StartsWith("<>", StringComparison.Ordinal)) { c._op = Op.Ne; len = 2; }
            else if (s.StartsWith("<=", StringComparison.Ordinal)) { c._op = Op.Le; len = 2; }
            else if (s.StartsWith(">=", StringComparison.Ordinal)) { c._op = Op.Ge; len = 2; }
            else if (s.StartsWith('=')) { c._op = Op.Eq; len = 1; }
            else if (s.StartsWith('<')) { c._op = Op.Lt; len = 1; }
            else if (s.StartsWith('>')) { c._op = Op.Gt; len = 1; }
            var operand = s[len..];

            if (operand.Length == 0 && c._op is Op.Eq or Op.Ne)
            {
                c._shape = c._op == Op.Eq ? Shape.Empty : Shape.NotEmpty;                                 // "", "=" and "<>"
                return c;
            }
            if (NumberGrammar.TryParse(operand, out double n)) { c._shape = Shape.Number; c._number = n; return c; }
            if (operand.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) { c._shape = Shape.Boolean; c._boolean = true; return c; }
            if (operand.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) { c._shape = Shape.Boolean; c._boolean = false; return c; }

            c._shape = Shape.Text;
            c._text = CellValue.FromText(operand);
            if (c._op is Op.Eq or Op.Ne) c._pattern = new WildcardMatcher.Compiled(operand.ToUpperInvariant());
            return c;
        }

        public bool Matches(CellValue cell)
        {
            if (cell.Kind == ValueKind.Error) return false;
            switch (_shape)
            {
                case Shape.Empty:
                    return cell.Kind == ValueKind.Blank || (cell.Kind == ValueKind.Text && cell.Text.Length == 0);
                case Shape.NotEmpty:
                    return !(cell.Kind == ValueKind.Blank || (cell.Kind == ValueKind.Text && cell.Text.Length == 0));
                case Shape.Number:
                    return cell.Kind == ValueKind.Number && Test(cell.Number.CompareTo(_number));
                case Shape.Boolean:
                    return cell.Kind == ValueKind.Boolean && Test(cell.Boolean.CompareTo(_boolean));
                default:
                    if (_op == Op.Ne) return !(cell.Kind == ValueKind.Text && _pattern!.IsMatch(cell.Text.ToUpperInvariant()));
                    if (cell.Kind != ValueKind.Text) return false;
                    if (_op == Op.Eq) return _pattern!.IsMatch(cell.Text.ToUpperInvariant());
                    return Test(ValueComparer.Compare(cell, _text));                                     // §6.3 text ordering
            }
        }

        private bool Test(int comparison) => _op switch
        {
            Op.Eq => comparison == 0,
            Op.Ne => comparison != 0,
            Op.Lt => comparison < 0,
            Op.Gt => comparison > 0,
            Op.Le => comparison <= 0,
            _ => comparison >= 0,
        };
    }
}
