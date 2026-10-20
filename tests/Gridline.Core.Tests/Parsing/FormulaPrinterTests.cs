using System.Text;
using Gridline.Core.Addressing;
using Gridline.Core.Parsing;

namespace Gridline.Core.Tests.Parsing;

// Requirement IDs: §5.5 (canonical formula printing), §9.4 (shift by offset, $ fixed, off-grid -> #REF!),
// §9.5 (range corners adjusted together), §6.7 / R9 / R10 / D-13 (no recursion along operator chains).
public class FormulaPrinterTests
{
    /// <summary>Parses text that may start with '=' (stripped) and prints it canonically.</summary>
    private static string P(string formula, IReferenceRewriter? rewriter = null, INameCasingResolver? names = null)
    {
        var text = formula.StartsWith('=') ? formula[1..] : formula;
        var parsed = FormulaParser.Parse(text, null);
        Assert.True(parsed.Success, $"'{formula}': {parsed.ErrorMessage} at {parsed.ErrorPosition}");
        return FormulaPrinter.Print(parsed.Root!, rewriter, names);
    }

    /// <summary>Asserts the printed text re-parses and prints identically.</summary>
    private static void AssertStable(string printed)
    {
        Assert.StartsWith("=", printed);
        Assert.Equal(printed, P(printed));
    }

    private sealed class IdentityRewriter : IReferenceRewriter
    {
        public CellReference? Rewrite(CellReference reference) => reference;
    }

    /// <summary>§9.4-style shift for the tests: relative parts move, $ parts stay, off-grid is invalid.</summary>
    private sealed class ShiftRewriter(int dRow, int dCol) : IReferenceRewriter
    {
        public CellReference? Rewrite(CellReference r)
        {
            int row = r.RowAbsolute ? r.Address.Row : r.Address.Row + dRow;
            int col = r.ColumnAbsolute ? r.Address.Column : r.Address.Column + dCol;
            if (row < 1 || row > GridBounds.MaxRows || col < 1 || col > GridBounds.MaxColumns) return null;
            return r with { Address = new CellAddress(col, row) };
        }
    }

    /// <summary>§9.5-style delete of rows [first, last] for the tests, overriding the range hook.</summary>
    private sealed class DeleteRowsRewriter(int first, int last) : IReferenceRewriter
    {
        private int Count => last - first + 1;

        public CellReference? Rewrite(CellReference r)
        {
            int x = r.Address.Row;
            if (x >= first && x <= last) return null;
            return x > last ? r with { Address = new CellAddress(r.Address.Column, x - Count) } : r;
        }

        public (CellReference Start, CellReference End)? RewriteRange(CellReference start, CellReference end)
        {
            int top = Math.Min(start.Address.Row, end.Address.Row), bottom = Math.Max(start.Address.Row, end.Address.Row);
            int newTop = top >= first && top <= last ? last + 1 : top;
            int newBottom = bottom >= first && bottom <= last ? first - 1 : bottom;
            if (newTop > newBottom) return null;
            int Shift(int row) => row > last ? row - Count : row;
            return (start with { Address = new CellAddress(start.Address.Column, Shift(newTop)) },
                    end with { Address = new CellAddress(end.Address.Column, Shift(newBottom)) });
        }
    }

    private sealed class Names(params string[] defined) : INameCasingResolver
    {
        public string? GetDefinedCasing(string name)
            => defined.FirstOrDefault(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));
    }

    // §5.5 worked example, verbatim.
    [Fact]
    public void WorkedExample_NoOpRewrite()
    {
        const string input = "= sum( a1 , $b$2:C3 ) * (2.50+ x)";
        const string expected = "=SUM(A1,$B$2:C3)*(2.50+x)";
        Assert.Equal(expected, P(input, new IdentityRewriter()));
        Assert.Equal(expected, P(input));
        Assert.Equal(expected, P(input, new IdentityRewriter(), new Names("Other")));
    }

    // §5.5 bullet 1: '=' prefix, no whitespace except inside string literals.
    [Theory]
    [InlineData(" 1 +\t2 ", "=1+2")]
    [InlineData("\" a  b \" & 1", "=\" a  b \"&1")]
    [InlineData("  sum(  1 ,  2  )  ", "=SUM(1,2)")]
    public void PrefixAndNoWhitespace(string input, string expected) => Assert.Equal(expected, P(input));

    // §5.5 bullet 2: function names, TRUE/FALSE, error literals and references upper-case; $ kept per reference.
    [Theory]
    [InlineData("sum(a1,true,false)", "=SUM(A1,TRUE,FALSE)")]
    [InlineData("iF(tRuE,#N/A,#DIV/0!)", "=IF(TRUE,#N/A,#DIV/0!)")]
    [InlineData("$a$1+b$2+$c3+d4", "=$A$1+B$2+$C3+D4")]
    [InlineData("#NAME?&#VALUE!&#REF!&#NUM!&#CIRC!&#PARSE!", "=#NAME?&#VALUE!&#REF!&#NUM!&#CIRC!&#PARSE!")]
    [InlineData("zz100000", "=ZZ100000")]
    [InlineData("foo(1)", "=FOO(1)")]
    public void UpperCaseAndDollarMarkers(string input, string expected) => Assert.Equal(expected, P(input));

    // §5.5 bullet 3: names use the definition's casing, or the casing as written when undefined.
    [Fact]
    public void NameCasing()
    {
        Assert.Equal("=TaxRate*myVar+AAA1", P("taxrate * myVar + AAA1", names: new Names("TaxRate")));
        Assert.Equal("=taxrate*myVar+AAA1", P("taxrate * myVar + AAA1"));
        Assert.Equal("=A100001", P("A100001", names: new Names("b1")));
    }

    [Fact]
    public void NameResolverReturningAnotherName_Throws()
    {
        var root = FormulaParser.Parse("x", null).Root!;
        Assert.Throws<InvalidOperationException>(() => FormulaPrinter.Print(root, null, new WrongNames()));
    }

    private sealed class WrongNames : INameCasingResolver
    {
        public string? GetDefinedCasing(string name) => "y";
    }

    // §5.5 bullet 4: number lexemes exactly as written (R53: 1e999 too).
    [Theory]
    [InlineData("1.50")]
    [InlineData("1e5")]
    [InlineData("1E+05")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData("007")]
    [InlineData("0.000")]
    [InlineData("1e999")]
    [InlineData("2.5e-3")]
    public void NumberLexemesKept(string lexeme)
    {
        Assert.Equal("=" + lexeme, P(" " + lexeme + " "));
        Assert.Equal("=-" + lexeme + "%", P("- " + lexeme + " %"));
    }

    // §5.5 bullet 5: strings re-emitted with "" escaping.
    [Theory]
    [InlineData("\"say \"\"hi\"\"\"")]
    [InlineData("\"\"\"\"")]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    [InlineData("\"a\"\"\"\"b\"")]
    public void StringsReEscaped(string literal)
    {
        Assert.Equal("=" + literal, P(literal));
        Assert.Equal("=" + literal + "&" + literal, P(literal + " & " + literal));
    }

    [Fact]
    public void StringNodeValueIsEscapedOnPrint()
        => Assert.Equal("=\"a\"\"b\"", FormulaPrinter.Print(new StringNode("a\"b")));

    // §5.5 bullet 6: parentheses exactly as written, nested and redundant ones included.
    [Theory]
    [InlineData("((1))+(2*(3))", "=((1))+(2*(3))")]
    [InlineData("(((A1)))", "=(((A1)))")]
    [InlineData("-(1)%", "=-(1)%")]
    [InlineData("SUM((A1:B2))", "=SUM((A1:B2))")]
    [InlineData("( 1 + 2 ) * 3", "=(1+2)*3")]
    [InlineData("1+2*3", "=1+2*3")] // none added
    [InlineData("(1+2)^(-(3))", "=(1+2)^(-(3))")]
    public void ParenthesesPreserved(string input, string expected) => Assert.Equal(expected, P(input));

    // §5.5 bullet 2, per corner: each range corner keeps its own $ markers, also under a shift.
    [Fact]
    public void DollarMarkersKeptPerCorner()
    {
        Assert.Equal("=$A1:B$2", P("$a1:b$2"));
        Assert.Equal("=SUM($A$1:C3,A$1:$B2)", P("sum($A$1:c3,a$1:$b2)"));
        Assert.Equal("=$A2:C$2", P("$A1:B$2", new ShiftRewriter(1, 1)));
        Assert.Equal("=B2+$A$1+$A2+B$1", P("A1+$A$1+$A1+A$1", new ShiftRewriter(1, 1)));
    }

    // §5.5 bullet 7: an invalid single reference prints #REF!, and the result re-parses as an error literal.
    [Fact]
    public void InvalidSingleReference_PrintsRef()
    {
        var printed = P("A1+B2*$C$1", new ShiftRewriter(-1, 0));
        Assert.Equal("=#REF!+B1*$C$1", printed);
        AssertStable(printed);
        var reparsed = FormulaParser.Parse(printed[1..], null).Root!;
        var left = Assert.IsType<BinaryNode>(reparsed).Left;
        Assert.Equal(ErrorCodes.Ref, Assert.IsType<ErrorNode>(left).Code);
    }

    // §5.5 bullet 7: if either corner of a range became invalid, the whole range prints as #REF!.
    [Theory]
    [InlineData("SUM(A1:B5)", "=SUM(#REF!)")]
    [InlineData("SUM(B5:A1)", "=SUM(#REF!)")]
    [InlineData("SUM($A$1:A1)", "=SUM(#REF!)")] // the fixed corner survives, the relative one moves to row 0
    [InlineData("SUM($A$1:A2)", "=SUM($A$1:A1)")]
    public void InvalidRangeCorner_PrintsRef(string input, string expected)
    {
        var printed = P(input, new ShiftRewriter(-1, 0));
        Assert.Equal(expected, printed);
        AssertStable(printed);
    }

    [Fact]
    public void ShiftOffRightEdge_PrintsRef()
        => Assert.Equal("=#REF!+$ZZ1+#REF!", P("ZZ1+$ZZ1+ZY1:ZZ2", new ShiftRewriter(0, 1)));

    // §9.5 needs both corners together: the range hook keeps surviving rows; single references in the span go #REF!.
    [Fact]
    public void RangeHook_AdjustsCornersTogether()
    {
        var del34 = new DeleteRowsRewriter(3, 4);
        Assert.Equal("=SUM(A1:A8)+#REF!+A3", P("SUM(A1:A10)+A3+A5", del34));
        var del12 = new DeleteRowsRewriter(1, 2);
        Assert.Equal("=SUM(A1:A8)", P("SUM(A1:A10)", del12));
        Assert.Equal("=SUM(#REF!)", P("SUM(A1:B2)", del12));
    }

    [Fact]
    public void DefaultRangeHook_ComposesCornerRewrites()
    {
        IReferenceRewriter shift = new ShiftRewriter(2, 0);
        var r = shift.RewriteRange(CellReference.Parse("A1"), CellReference.Parse("$B$2"));
        Assert.Equal((CellReference.Parse("A3"), CellReference.Parse("$B$2")), r);
        Assert.Null(shift.RewriteRange(CellReference.Parse("A1"), CellReference.Parse("A99999")));
    }

    // §5.3 omitted arguments print as empty.
    [Theory]
    [InlineData("IF(A1,,3)", "=IF(A1,,3)")]
    [InlineData("if( , , )", "=IF(,,)")]
    [InlineData("IF(A1,)", "=IF(A1,)")]
    [InlineData("PI()", "=PI()")]
    [InlineData("COUNT(1,,2)", "=COUNT(1,,2)")]
    public void OmittedArguments(string input, string expected) => Assert.Equal(expected, P(input));

    // Unary and postfix operators without spaces; every binary operator.
    [Theory]
    [InlineData(" - - 1 % %", "=--1%%")]
    [InlineData("+ A1", "=+A1")]
    [InlineData("1 - -2", "=1--2")]
    [InlineData("1 < -1", "=1<-1")]
    [InlineData("1=1", "=1=1")]
    [InlineData("1 <> 2", "=1<>2")]
    [InlineData("1 <= 2", "=1<=2")]
    [InlineData("1 >= 2", "=1>=2")]
    [InlineData("1 > 2", "=1>2")]
    [InlineData("1 ^ 2 / 3 & \"x\"", "=1^2/3&\"x\"")]
    public void Operators(string input, string expected)
    {
        Assert.Equal(expected, P(input));
        AssertStable(expected);
    }

    // Error literals print as stored; a '/' straight after one re-parses (tokenizer lets '/' end the literal).
    [Theory]
    [InlineData("#N/A / 2", "=#N/A/2")]
    [InlineData("#DIV/0! / #N/A", "=#DIV/0!/#N/A")]
    [InlineData("#NAME? / 1", "=#NAME?/1")]
    public void ErrorLiteralFollowedByDivide(string input, string expected)
    {
        Assert.Equal(expected, P(input));
        AssertStable(expected);
    }

    [Fact]
    public void InvalidReferenceBeforeDivide_ReParses()
    {
        var printed = P("A1/2", new ShiftRewriter(-1, 0));
        Assert.Equal("=#REF!/2", printed);
        AssertStable(printed);
    }

    [Fact]
    public void NullRoot_Throws() => Assert.Throws<ArgumentNullException>(() => FormulaPrinter.Print(null!));

    // D-13, §6.7, R9: a 16,000-term chain (left spine ~16,000 deep) prints on a 1 MB thread.
    [Fact]
    public void LongChain_PrintsOnOneMegabyteThread()
    {
        var sb = new StringBuilder("1");
        for (int i = 1; i < 16000; i++) sb.Append("+1");
        string text = sb.ToString();
        string? printed = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var parsed = FormulaParser.Parse(text, null);
                printed = FormulaPrinter.Print(parsed.Root!, new IdentityRewriter());
            }
            catch (Exception ex) { failure = ex; }
        }, 1024 * 1024);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.Equal("=" + text, printed);
    }

    [Fact]
    public void MaxNesting_PrintsOnSmallThread()
    {
        string text = new string('(', 1024) + "1" + new string(')', 1024);
        string? printed = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { printed = FormulaPrinter.Print(FormulaParser.Parse(text, null).Root!); }
            catch (Exception ex) { failure = ex; }
        }, 256 * 1024);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.Equal("=" + text, printed);
    }
}
