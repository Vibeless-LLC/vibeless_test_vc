using System.Text;
using FsCheck;
using FsCheck.Fluent;
using Gridline.Core.Addressing;
using Gridline.Core.Parsing;

namespace Gridline.Core.Tests.Parsing;

// Requirement IDs: §5.5 (canonical printing re-parses and is a fixed point), §5.1-§5.3 (generated formula forms).
public class FormulaPrinterPropertyTests
{
    private static readonly Gen<string> Ws = Gen.Elements("", "", " ", "  ", "\t");

    private static Gen<string> Padded(Gen<string> g) =>
        from a in Ws
        from x in g
        from b in Ws
        select a + x + b;

    private static readonly Gen<string> NumberGen =
        Gen.Elements("0", "1", "1.50", "1e5", "1E+05", ".5", "1.", "007", "2.5e-3", "1e999", "123456789.000");

    private static readonly Gen<string> StringGen =
        Gen.Elements('a', 'Z', ' ', '"', 'é', '\t', ',', '(', '#').ArrayOf()
            .Select(cs => "\"" + new string(cs).Replace("\"", "\"\"") + "\"");

    private static readonly Gen<string> BoolGen = Gen.Elements("TRUE", "false", "True", "fAlSe");

    private static readonly Gen<string> ErrorGen =
        Gen.Elements("#DIV/0!", "#VALUE!", "#REF!", "#NAME?", "#N/A", "#NUM!", "#CIRC!", "#PARSE!");

    private static readonly Gen<string> RefGen =
        from colAbs in Gen.Elements("", "$")
        from col in Gen.Elements("a", "B", "zz", "Zy", "AB")
        from rowAbs in Gen.Elements("", "$")
        from row in Gen.Elements("1", "2", "99", "100000")
        select colAbs + col + rowAbs + row;

    private static readonly Gen<string> RangeGen =
        from a in RefGen
        from b in RefGen
        select a + ":" + b;

    private static readonly Gen<string> NameGen = Gen.Elements("x", "Rate", "my_name", "a.b", "AAA1", "A100001", "tax_2");

    private static readonly Gen<string> FuncNameGen = Gen.Elements("sum", "If", "ABS", "foo", "Concat", "pi");

    private static readonly Gen<string> BinOpGen = Gen.Elements("+", "-", "*", "/", "^", "&", "=", "<>", "<", ">", "<=", ">=");

    private static readonly Gen<string> Leaf =
        Padded(Gen.OneOf(NumberGen, StringGen, BoolGen, ErrorGen, RefGen, RangeGen, NameGen));

    private static Gen<string> Expr(int depth)
    {
        if (depth <= 0) return Leaf;
        var sub = Expr(depth - 1);
        var binary = from l in sub from op in BinOpGen from r in sub select l + op + r;
        var unary = from op in Gen.Elements("-", "+") from w in Ws from e in sub select op + w + e;
        var percent = from e in sub from w in Ws select e + w + "%";
        var paren = from e in sub select "(" + e + ")";
        var arg = Gen.OneOf(sub, Ws); // whitespace only -> omitted argument
        var call =
            from name in FuncNameGen
            from args in Gen.Choose(0, 3).SelectMany(n => arg.ArrayOf(n))
            from w in Ws
            select name + "(" + (args.Length == 0 ? w : string.Join(",", args)) + ")";
        return Gen.Frequency((3, Leaf), (3, binary), (1, unary), (1, percent), (1, paren), (2, call));
    }

    private static bool HasWhitespaceOutsideStrings(string s)
    {
        bool inString = false;
        foreach (char c in s)
        {
            if (c == '"') inString = !inString;
            else if (!inString && (c == ' ' || c == '\t')) return true;
        }
        return false;
    }

    [Fact]
    public void PrintOfParse_ReParses_AndPrintingIsIdempotent()
    {
        Prop.ForAll(Expr(4).ToArbitrary(), f =>
        {
            var first = FormulaParser.Parse(f, null);
            if (!first.Success) return false.Label($"generated formula did not parse: {f} ({first.ErrorMessage})");
            string p1 = FormulaPrinter.Print(first.Root!);
            if (!p1.StartsWith('=') || HasWhitespaceOutsideStrings(p1)) return false.Label($"not canonical: {p1}");
            var second = FormulaParser.Parse(p1[1..], null);
            if (!second.Success) return false.Label($"printed form did not parse: {p1} ({second.ErrorMessage})");
            string p2 = FormulaPrinter.Print(second.Root!);
            return (p1 == p2).Label($"not idempotent: {p1} -> {p2}");
        }).QuickCheckThrowOnFailure();
    }

    /// <summary>Maps references in row 2 or column B to invalid, others unchanged (so #REF! lands before every operator).</summary>
    private sealed class SomeInvalid : IReferenceRewriter
    {
        public static bool IsInvalid(CellReference r) => r.Address.Row == 2 || r.Address.Column == 2;
        public CellReference? Rewrite(CellReference reference) => IsInvalid(reference) ? null : reference;
    }

    /// <summary>
    /// Structural AST dump: node kinds, payloads (number lexeme, string value, name as written, reference with $ flags)
    /// and explicit parentheses, with no whitespace. With <paramref name="invalidate"/>, references it rejects (and ranges
    /// with a rejected corner) dump as the #REF! error node they should print as.
    /// </summary>
    private static string Dump(FormulaNode node, Func<CellReference, bool>? invalidate = null)
    {
        var sb = new StringBuilder();
        Write(node);
        return sb.ToString();

        void Write(FormulaNode n)
        {
            switch (n)
            {
                case NumberNode x: sb.Append("Num(").Append(x.Lexeme).Append(')'); break;
                case StringNode x: sb.Append("Str(").Append(x.Value.Length).Append(':').Append(x.Value).Append(')'); break;
                case BooleanNode x: sb.Append(x.Value ? "True" : "False"); break;
                case ErrorNode x: sb.Append("Err(").Append(x.Code).Append(')'); break;
                case CellRefNode x when invalidate?.Invoke(x.Reference) == true: sb.Append("Err(#REF!)"); break;
                case CellRefNode x: sb.Append("Ref(").Append(x.Reference).Append(')'); break;
                case RangeNode x when invalidate is not null && (invalidate(x.Start) || invalidate(x.End)): sb.Append("Err(#REF!)"); break;
                case RangeNode x: sb.Append("Range(").Append(x.Start).Append(',').Append(x.End).Append(')'); break;
                case NameNode x: sb.Append("Name(").Append(x.Name).Append(')'); break;
                case OmittedArgNode: sb.Append("Omitted"); break;
                case UnaryNode x: sb.Append(x.Operator).Append('('); Write(x.Operand); sb.Append(')'); break;
                case PercentNode x: sb.Append("Pct("); Write(x.Operand); sb.Append(')'); break;
                case ParenNode x: sb.Append("Paren("); Write(x.Inner); sb.Append(')'); break;
                case BinaryNode x: sb.Append(x.Operator).Append('('); Write(x.Left); sb.Append(','); Write(x.Right); sb.Append(')'); break;
                case FunctionCallNode x:
                    sb.Append("Call(").Append(x.Name);
                    foreach (var a in x.Arguments) { sb.Append(','); Write(a); }
                    sb.Append(')');
                    break;
                default: throw new InvalidOperationException(n.GetType().Name);
            }
        }
    }

    // The printed form must have the same tree as the source: dropped parentheses or reordered operands fail here.
    [Fact]
    public void PrintOfParse_HasTheSameAst()
    {
        Prop.ForAll(Expr(4).ToArbitrary(), f =>
        {
            var first = FormulaParser.Parse(f, null);
            if (!first.Success) return false.Label($"generated formula did not parse: {f}");
            string p1 = FormulaPrinter.Print(first.Root!);
            var second = FormulaParser.Parse(p1[1..], null);
            if (!second.Success) return false.Label($"printed form did not parse: {p1}");
            string a = Dump(first.Root!), b = Dump(second.Root!);
            return (a == b).Label($"AST changed: {f} -> {p1}: {a} vs {b}");
        }).QuickCheckThrowOnFailure();
    }

    // With some references rewritten to invalid, #REF! (single or whole range) re-parses wherever it lands, the tree is the
    // source tree with those references replaced by the #REF! error literal, and printing again is idempotent.
    [Fact]
    public void RewrittenInvalidReferences_ReParseAsRefErrors()
    {
        var rewriter = new SomeInvalid();
        Prop.ForAll(Expr(4).ToArbitrary(), f =>
        {
            var first = FormulaParser.Parse(f, null);
            if (!first.Success) return false.Label($"generated formula did not parse: {f}");
            string p1 = FormulaPrinter.Print(first.Root!, rewriter);
            var second = FormulaParser.Parse(p1[1..], null);
            if (!second.Success) return false.Label($"printed form did not parse: {p1} ({second.ErrorMessage})");
            string expected = Dump(first.Root!, SomeInvalid.IsInvalid), actual = Dump(second.Root!);
            if (expected != actual) return false.Label($"AST mismatch: {p1}: {expected} vs {actual}");
            return (FormulaPrinter.Print(second.Root!) == p1).Label($"not idempotent: {p1}");
        }).QuickCheckThrowOnFailure();
    }

    [Theory]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("*")]
    [InlineData("/")]
    [InlineData("^")]
    [InlineData("&")]
    [InlineData("=")]
    [InlineData("<>")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("<=")]
    [InlineData(">=")]
    public void RefErrorBeforeEachOperator_ReParses(string op)
    {
        var root = FormulaParser.Parse("A2" + op + "B1:C3" + op + "A1", null).Root!;
        var printed = FormulaPrinter.Print(root, new SomeInvalid());
        Assert.Equal("=#REF!" + op + "#REF!" + op + "A1", printed);
        var reparsed = FormulaParser.Parse(printed[1..], null);
        Assert.True(reparsed.Success, reparsed.ErrorMessage);
        Assert.Equal(printed, FormulaPrinter.Print(reparsed.Root!));
    }

    [Fact]
    public void RefErrorBeforePercentAndInsideCall_ReParses()
    {
        var root = FormulaParser.Parse("-A2%+SUM(B5,A2:A9,,A1)", null).Root!;
        var printed = FormulaPrinter.Print(root, new SomeInvalid());
        Assert.Equal("=-#REF!%+SUM(#REF!,#REF!,,A1)", printed);
        Assert.Equal(printed, FormulaPrinter.Print(FormulaParser.Parse(printed[1..], null).Root!));
    }
}
