using FsCheck;
using FsCheck.Fluent;
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
}
