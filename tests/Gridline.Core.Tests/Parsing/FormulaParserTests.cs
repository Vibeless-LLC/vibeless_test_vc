using System.Text;
using Gridline.Core.Parsing;

namespace Gridline.Core.Tests.Parsing;

// Requirement IDs: §5.2 (operator precedence), §5.3 (formula grammar), §5.4 (parse errors -> #PARSE!), R3 (arity), R8 (depth cap).
public class FormulaParserTests
{
    private sealed class FakeArities : IFunctionArityProvider
    {
        private readonly Dictionary<string, (int Min, int Max)> _table = new(StringComparer.Ordinal)
        {
            ["IF"] = (2, 3),
            ["ABS"] = (1, 1),
            ["PI"] = (0, 0),
            ["SUM"] = (1, 255),
        };

        public bool TryGetArity(string upperName, out int min, out int max)
        {
            if (_table.TryGetValue(upperName, out var a)) { min = a.Min; max = a.Max; return true; }
            min = max = 0;
            return false;
        }
    }

    private static readonly FakeArities Arities = new();

    private static string Shape(string formulaText) => Shape(formulaText, Arities);

    /// <summary>Uses exactly the given provider; an explicit null means "no arity checking" and is honoured.</summary>
    private static string Shape(string formulaText, IFunctionArityProvider? arities)
    {
        var r = FormulaParser.Parse(formulaText, arities);
        Assert.True(r.Success, $"'{formulaText}': {r.ErrorMessage} at {r.ErrorPosition}");
        Assert.NotNull(r.Root);
        Assert.Equal("", r.ErrorMessage);
        Assert.Equal(-1, r.ErrorPosition);
        return Print(r.Root!);
    }

    private static ParsedFormula Fails(string formulaText, int? position = null) => Fails(formulaText, position, Arities);

    /// <summary>Uses exactly the given provider; an explicit null means "no arity checking" and is honoured.</summary>
    private static ParsedFormula Fails(string formulaText, int? position, IFunctionArityProvider? arities)
    {
        var r = FormulaParser.Parse(formulaText, arities);
        Assert.False(r.Success, $"'{formulaText}' unexpectedly parsed");
        Assert.Null(r.Root);
        Assert.False(string.IsNullOrEmpty(r.ErrorMessage));
        Assert.True(r.ErrorPosition >= 0 && r.ErrorPosition <= formulaText.Length);
        if (position is { } p) Assert.Equal(p, r.ErrorPosition);
        return r;
    }

    private static string Op(BinaryOperator op) => op switch
    {
        BinaryOperator.Add => "+",
        BinaryOperator.Subtract => "-",
        BinaryOperator.Multiply => "*",
        BinaryOperator.Divide => "/",
        BinaryOperator.Power => "^",
        BinaryOperator.Concat => "&",
        BinaryOperator.Equal => "=",
        BinaryOperator.NotEqual => "<>",
        BinaryOperator.Less => "<",
        BinaryOperator.Greater => ">",
        BinaryOperator.LessEqual => "<=",
        BinaryOperator.GreaterEqual => ">=",
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    private static string Print(FormulaNode node) => node switch
    {
        NumberNode n => n.Lexeme,
        StringNode s => $"Str({s.Value})",
        BooleanNode b => b.Value ? "TRUE" : "FALSE",
        ErrorNode e => e.Code,
        CellRefNode c => $"Ref({c.Reference})",
        RangeNode r => $"Range({r.Start},{r.End})",
        NameNode n => $"Name({n.Name})",
        OmittedArgNode => "Omitted",
        FunctionCallNode f => f.Arguments.Count == 0
            ? $"Call({f.Name})"
            : $"Call({f.Name},{string.Join(",", f.Arguments.Select(Print))})",
        UnaryNode u => $"Unary({(u.Operator == UnaryOperator.Negate ? "-" : "+")},{Print(u.Operand)})",
        PercentNode p => $"Percent({Print(p.Operand)})",
        BinaryNode b => $"Binary({Op(b.Operator)},{Print(b.Left)},{Print(b.Right)})",
        ParenNode p => $"Paren({Print(p.Inner)})",
        _ => throw new InvalidOperationException(node.GetType().Name),
    };

    // ---- §5.2 precedence and associativity (acceptance shapes) ----

    [Fact] public void UnaryMinusBindsTighterThanPower() => Assert.Equal("Binary(^,Unary(-,2),2)", Shape("-2^2"));
    [Fact] public void PowerIsLeftAssociative() => Assert.Equal("Binary(^,Binary(^,2,3),2)", Shape("2^3^2"));
    [Fact] public void AdditionBindsTighterThanConcat() => Assert.Equal("Binary(&,Binary(+,1,2),3)", Shape("1+2&3"));
    [Fact] public void ConcatBindsTighterThanComparison() => Assert.Equal("Binary(=,Binary(&,Str(a),1),1)", Shape("\"a\"&1=1"));
    [Fact] public void DoublePercentNests() => Assert.Equal("Percent(Percent(50))", Shape("50%%"));
    [Fact] public void UnaryBindsTighterThanPercent() => Assert.Equal("Percent(Unary(-,50))", Shape("-50%"));
    [Fact] public void UnaryInPowerExponent() => Assert.Equal("Binary(^,2,Unary(-,2))", Shape("2^-2"));
    [Fact] public void PercentBindsTighterThanPower() => Assert.Equal("Binary(^,2,Percent(50))", Shape("2^50%"));
    [Fact] public void MultiplicationBindsTighterThanAddition() => Assert.Equal("Binary(+,1,Binary(*,2,3))", Shape("1+2*3"));
    [Fact] public void SubtractionIsLeftAssociative() => Assert.Equal("Binary(-,Binary(-,1,2),3)", Shape("1-2-3"));
    [Fact] public void DivisionIsLeftAssociative() => Assert.Equal("Binary(/,Binary(/,8,4),2)", Shape("8/4/2"));
    [Fact] public void ComparisonsAreLeftAssociative() => Assert.Equal("Binary(<>,Binary(=,1,2),3)", Shape("1=2<>3"));
    [Fact] public void MixedLevels() => Assert.Equal("Binary(+,Binary(*,1,2),Binary(*,3,Binary(^,4,5)))", Shape("1*2+3*4^5"));
    [Fact] public void ParensAreKept() => Assert.Equal("Binary(*,Paren(Binary(+,1,2)),3)", Shape("(1+2)*3"));
    [Fact] public void RedundantParensAreKept() => Assert.Equal("Paren(Paren(1))", Shape("((1))"));
    [Fact] public void UnaryChain() => Assert.Equal("Unary(-,Unary(+,Unary(-,1)))", Shape("-+-1"));
    [Fact] public void UnaryAppliesToParenGroup() => Assert.Equal("Unary(-,Paren(Binary(^,2,2)))", Shape("-(2^2)"));
    [Fact] public void UnaryBindsTighterThanMultiply() => Assert.Equal("Binary(*,Unary(-,1),Unary(-,2))", Shape("-1*-2"));
    // Stack-machine regression shapes (§5.2, §5.3).
    [Fact] public void CallWithParenthesisedArgument() =>
        Assert.Equal("Call(SUM,1,Binary(*,Paren(Binary(+,2,3)),4))", Shape("SUM(1,(2+3)*4)"));
    [Fact] public void RangeThenAddition() => Assert.Equal("Binary(+,Range(A1,B2),1)", Shape("A1:B2+1"));
    [Fact] public void DoubleNegationInParens() => Assert.Equal("Unary(-,Paren(Unary(-,1)))", Shape("-(-1)"));
    [Fact] public void PercentInsidePowerInsideSum() =>
        Assert.Equal("Binary(+,1,Binary(*,2,Binary(^,3,Percent(2))))", Shape("1+2*3^2%"));
    [Fact] public void UnaryRightOfMultiply() => Assert.Equal("Binary(*,2,Binary(^,Unary(-,3),2))", Shape("2*-3^2"));
    [Fact] public void WhitespaceIsIgnored() => Assert.Equal("Binary(+,1,Binary(*,2,3))", Shape(" 1 + 2 *\t3 "));

    [Theory]
    [InlineData("1=2", "=")]
    [InlineData("1<>2", "<>")]
    [InlineData("1<2", "<")]
    [InlineData("1>2", ">")]
    [InlineData("1<=2", "<=")]
    [InlineData("1>=2", ">=")]
    public void EveryComparisonOperator(string text, string op) => Assert.Equal($"Binary({op},1,2)", Shape(text));

    // ---- §5.3 primaries ----

    [Fact]
    public void NumberKeepsLexemeAndValue()
    {
        var r = FormulaParser.Parse("1.50", null);
        var n = Assert.IsType<NumberNode>(r.Root);
        Assert.Equal(1.5, n.Value);
        Assert.Equal("1.50", n.Lexeme);
        var e = Assert.IsType<NumberNode>(FormulaParser.Parse(".5E1", null).Root);
        Assert.Equal(5.0, e.Value);
        Assert.Equal(".5E1", e.Lexeme);
    }

    [Fact] public void StringLiteralIsUnescaped() => Assert.Equal("Str(say \"hi\")", Shape("\"say \"\"hi\"\"\""));
    [Fact] public void BooleansParse() => Assert.Equal("Binary(=,TRUE,FALSE)", Shape("true=FALSE"));
    [Fact] public void ErrorLiteralIsCanonical() => Assert.Equal("#N/A", Shape("#n/a"));
    [Fact] public void NameParses() => Assert.Equal("Binary(+,Name(myName),1)", Shape("myName+1"));
    [Fact] public void CellRefKeepsDollarFlags() => Assert.Equal("Ref($B$7)", Shape("$b$7"));

    [Fact]
    public void RangeKeepsRefsAsWritten()
    {
        Assert.Equal("Range(B2,$A$1)", Shape("B2:$A$1"));
        var range = Assert.IsType<RangeNode>(FormulaParser.Parse("b$2:$a1", null).Root);
        Assert.False(range.Start.ColumnAbsolute);
        Assert.True(range.Start.RowAbsolute);
        Assert.True(range.End.ColumnAbsolute);
        Assert.False(range.End.RowAbsolute);
        Assert.Equal(2, range.Start.Address.Column);
        Assert.Equal(1, range.End.Address.Row);
    }

    [Fact] public void RangeBindsTighterThanUnary() => Assert.Equal("Unary(-,Range(A1,B2))", Shape("-A1:B2"));
    [Fact] public void RangeAsFunctionArgument() => Assert.Equal("Call(SUM,Range(A1,B10))", Shape("sum(A1:B10)"));
    [Fact] public void RangeWithWhitespaceAroundColon() => Assert.Equal("Range(A1,B2)", Shape("A1 : B2"));

    // ---- §5.3 function calls and omitted arguments ----

    [Fact] public void ZeroArgCall() => Assert.Equal("Call(PI)", Shape("pi()"));
    [Fact] public void OmittedMiddleArgument() => Assert.Equal("Call(IF,Ref(A1),Omitted,3)", Shape("IF(A1,,3)"));
    [Fact] public void TwoOmittedArguments() => Assert.Equal("Call(F,Omitted,Omitted)", Shape("F(,)"));
    [Fact] public void TrailingOmittedArgument() => Assert.Equal("Call(F,1,Omitted)", Shape("F(1,)"));
    [Fact] public void LeadingOmittedArgument() => Assert.Equal("Call(IF,Omitted,2)", Shape("IF(,2)"));
    [Fact] public void NestedCalls() => Assert.Equal("Call(SUM,Call(ABS,Unary(-,1)),Binary(*,2,3))", Shape("SUM(ABS(-1),2*3)"));
    [Fact] public void CallInExpression() => Assert.Equal("Binary(*,Call(PI),2)", Shape("PI()*2"));
    [Fact] public void UnknownFunctionParses() => Assert.Equal("Call(NOSUCHFN,1,2,3,4,5)", Shape("NoSuchFn(1,2,3,4,5)"));
    [Fact] public void FunctionNamedLikeCellRefParses() => Assert.Equal("Call(A1,1)", Shape("A1(1)"));

    [Fact]
    public void ArgumentCounts()
    {
        Assert.Empty(Call("F()").Arguments);
        Assert.Equal(2, Call("F(,)").Arguments.Count);
        Assert.Equal(2, Call("F(1,)").Arguments.Count);
        Assert.Equal(3, Call("IF(A1,,3)").Arguments.Count);
        Assert.IsType<OmittedArgNode>(Call("F(1,)").Arguments[1]);
    }

    private static FunctionCallNode Call(string text) => Assert.IsType<FunctionCallNode>(FormulaParser.Parse(text, Arities).Root);

    [Fact]
    public void NullArityProviderIsHonouredByHelpers()
    {
        Assert.Equal("Call(ABS,1,2)", Shape("ABS(1,2)", null));   // would fail with the fake table
        Fails("ABS(1,2)");
        Fails("1+", null, null);
    }

    [Fact]
    public void NullArityProviderSkipsArityChecks()
    {
        var r = FormulaParser.Parse("ABS(1,2)", null);
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("Call(ABS,1,2)", Print(r.Root!));
        Assert.True(FormulaParser.Parse("IF()", null).Success);
    }

    // ---- R3 arity ----

    [Theory]
    [InlineData("IF(1)")]
    [InlineData("IF()")]
    [InlineData("IF(1,2,3,4)")]
    [InlineData("IF(1,,,)")]
    [InlineData("ABS()")]
    [InlineData("ABS(,)")]
    [InlineData("ABS(1,2)")]
    [InlineData("PI(1)")]
    [InlineData("PI(,)")]
    [InlineData("SUM()")]
    [InlineData("1+abs(1,2)")]
    public void KnownFunctionWithWrongArityIsParseError(string text) => Fails(text);

    [Fact] public void WrongArityReportsFunctionPosition() => Fails("1+ABS(1,2)", position: 2);

    [Theory]
    [InlineData("IF(1,2)")]
    [InlineData("IF(1,2,3)")]
    [InlineData("IF(A1,,3)")]
    [InlineData("IF(,)")]
    [InlineData("ABS(-1)")]
    [InlineData("PI()")]
    [InlineData("SUM(1,2,3)")]
    public void KnownFunctionWithinArityParses(string text) => Assert.True(FormulaParser.Parse(text, Arities).Success);

    [Fact]
    public void ArityIsCheckedInNestedCalls() => Fails("SUM(1,IF(2))", position: 6);

    // ---- §5.4 parse errors ----

    [Fact] public void EmptyFormulaIsParseError() => Fails("", position: 0);
    [Fact] public void WhitespaceOnlyFormulaIsParseError() => Fails("   ");
    [Fact] public void TrailingOperatorIsParseError() => Fails("1+", position: 2);
    [Fact] public void TokenizerFailureIsParseError() => Fails("1+@", position: 2);
    [Fact] public void UnterminatedStringIsParseError() => Fails("\"abc", position: 0);

    [Theory]
    [InlineData("A1:5")]
    [InlineData("(A1):B2")]
    [InlineData("A1:B2:C3")]
    [InlineData("x:y")]
    [InlineData("A1:x")]
    [InlineData("x:A1")]
    [InlineData("A1:")]
    [InlineData(":A1")]
    [InlineData("A1:(B2)")]
    [InlineData("A1:-B2")]
    [InlineData("SUM(A1):B2")]
    [InlineData("1:2")]
    public void ColonMisuseIsParseError(string text) => Fails(text);

    [Fact] public void ColonFollowedByNumberReportsNumberPosition() => Fails("A1:5", position: 3);

    [Theory]
    [InlineData("5abc", 1)]   // Number, Name
    [InlineData("5A1", 1)]    // Number, CellRef
    [InlineData("1.5.3", 3)]  // Number, Number
    [InlineData("A1 B2", 3)]
    [InlineData("\"a\"\"b\" 1", 7)]
    [InlineData("(1)(2)", 3)]
    [InlineData("$A$1(1)", 4)] // CellRef, LParen (D-12 correction)
    [InlineData("SUM (1)", 4)] // Name, LParen
    public void AdjacentOperandsAreParseErrors(string text, int position) => Fails(text, position);

    [Theory]
    [InlineData("(1")]
    [InlineData("((1)")]
    [InlineData("1)")]
    [InlineData(")")]
    [InlineData("()")]
    [InlineData("SUM(1")]
    [InlineData("SUM(1,")]
    [InlineData("SUM(1 2)")]
    [InlineData("1,2")]
    [InlineData("1,")]
    [InlineData(",")]
    [InlineData("(1,2)")]
    [InlineData("*1")]
    [InlineData("1+*2")]
    [InlineData("%")]
    [InlineData("1^")]
    [InlineData("-")]
    [InlineData("1=")]
    [InlineData("1&")]
    [InlineData("=1")]
    [InlineData("1==2")]
    public void MalformedExpressionsAreParseErrors(string text) => Fails(text);

    [Fact] public void UnclosedParenReportsEndPosition() => Fails("(1", position: 2);
    [Fact] public void StrayCloseParenReportsItsPosition() => Fails("1)", position: 1);

    [Fact]
    public void NeverThrowsForOddInputs()
    {
        string[] inputs = ["", "(", ")", ",", ":", "%%%", "\"", "#", "$", "$A", "A1:B2:C3", "SUM(,,,,", "-----", "1e999", "((((", "))))"];
        foreach (var s in inputs) _ = FormulaParser.Parse(s, Arities);
    }

    [Fact] public void NullFormulaTextThrows() => Assert.Throws<ArgumentNullException>(() => FormulaParser.Parse(null!, null));

    // ---- R8 depth cap ----

    private static string Nest(int depth, string open, string inner, string close)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < depth; i++) sb.Append(open);
        sb.Append(inner);
        for (int i = 0; i < depth; i++) sb.Append(close);
        return sb.ToString();
    }

    [Fact] public void MaxDepthIs1024() => Assert.Equal(1024, FormulaParser.MaxNestingDepth);

    [Fact]
    public void ThousandNestedParensParse()
    {
        var r = FormulaParser.Parse(Nest(1000, "(", "1", ")"), null);
        Assert.True(r.Success, r.ErrorMessage);
        int depth = 0;
        FormulaNode node = r.Root!;
        while (node is ParenNode p) { depth++; node = p.Inner; }
        Assert.Equal(1000, depth);
        Assert.IsType<NumberNode>(node);
    }

    [Fact] public void NestingAtTheCapParses() => Assert.True(FormulaParser.Parse(Nest(1024, "(", "1", ")"), null).Success);
    [Fact] public void NestingOneBeyondTheCapIsParseError() => Fails(Nest(1025, "(", "1", ")"), position: 1024);
    [Fact] public void TwoThousandNestedParensIsParseErrorWithoutCrashing() => Fails(Nest(2000, "(", "1", ")"));
    [Fact] public void MaxLengthNestedParensIsParseError() => Fails(new string('(', 32767));

    // R13: function calls nest at most 64 deep (the combined R8 cap of 1024 still applies).
    [Fact] public void MaxCallDepthIs64() => Assert.Equal(64, FormulaParser.MaxCallNestingDepth);
    [Fact] public void NestedCallsAtTheCallCapParse() => Assert.True(FormulaParser.Parse(Nest(64, "F(", "1", ")"), null).Success);
    [Fact] public void NestedCallsBeyondTheCallCapAreParseError() => Fails(Nest(65, "F(", "1", ")"), position: 128);
    [Fact] public void NestedCallsFarBeyondTheCallCapAreParseError() => Fails(Nest(1025, "F(", "1", ")"));
    [Fact] public void SiblingCallsDoNotAccumulateCallDepth() =>
        Assert.True(FormulaParser.Parse(Nest(62, "F(", "G(1)+G(2)+G(H(3))+G(H(4))", ")"), null).Success);
    [Fact] public void ParensDoNotCountAsCalls() =>
        Assert.True(FormulaParser.Parse(Nest(64, "F((", "1", "))"), null).Success);
    [Fact] public void CallCapCountsCallsInsideParens() => Fails(Nest(65, "(F(", "1", "))"), position: 64 * 3 + 1);
    [Fact] public void UnaryChainAtTheCapParses() => Assert.True(FormulaParser.Parse(new string('-', 1024) + "1", null).Success);
    [Fact] public void UnaryChainBeyondTheCapIsParseError() => Fails(new string('-', 1025) + "1", position: 1024);
    [Fact] public void LongUnaryChainIsParseErrorWithoutCrashing() => Fails(new string('-', 32766) + "1");
    [Fact] public void MixedNestingCountsTogether() => Fails(Nest(600, "(", Nest(425, "-", "1", ""), ")"));

    [Fact]
    public void DeepNestingAtTheCapDoesNotNeedALargeStack()
    {
        // The parser is iterative, so maximum nesting must parse even on a small (256 KB) thread stack.
        string[] inputs =
        [
            Nest(1024, "(", "1", ")"),
            Nest(64, "SUM(" + new string('(', 15), "1", new string(')', 15) + ")"), // 64 calls x 16 levels = 1024
            new string('-', 1024) + "1",
            Nest(512, "(", Nest(512, "-", "1", ""), ")"),
            Nest(64, "IF(1,", "2", ",3)"),
        ];
        var results = new bool[inputs.Length];
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < inputs.Length; i++) results[i] = FormulaParser.Parse(inputs[i], Arities).Success;
            }
            catch (Exception ex) { failure = ex; }
        }, 256 * 1024);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.All(results, Assert.True);
    }

    [Fact]
    public void LongFlatChainsDoNotCountTowardDepth()
    {
        var sb = new StringBuilder("1");
        for (int i = 0; i < 10000; i++) sb.Append("+1");
        var r = FormulaParser.Parse(sb.ToString(), null);
        Assert.True(r.Success, r.ErrorMessage);
        var top = Assert.IsType<BinaryNode>(r.Root);
        Assert.IsType<BinaryNode>(top.Left); // left-associative spine
        Assert.IsType<NumberNode>(top.Right);
    }

    [Fact]
    public void DepthIsRestoredAfterSiblingGroups()
    {
        // Three sibling groups each 1000 deep would be far over the cap if depth leaked across siblings.
        string group = Nest(1000, "(", "1", ")");
        var text = string.Join("+", Enumerable.Repeat(group, 3));
        Assert.True(FormulaParser.Parse(text, null).Success);
    }

    [Fact]
    public void ArgumentListIsImmutable()
    {
        var call = Call("SUM(1,2)");
        Assert.False(call.Arguments is FormulaNode[]);
        Assert.Throws<NotSupportedException>(() => ((IList<FormulaNode>)call.Arguments).Add(new NumberNode(1, "1")));
    }

}
