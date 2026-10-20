using System.Diagnostics;
using System.Text;
using Gridline.Core.Addressing;
using Gridline.Core.Evaluation;
using Gridline.Core.Parsing;
using static Gridline.Core.Tests.Evaluation.Eval;

namespace Gridline.Core.Tests.Evaluation;

// Requirement IDs: §6.4 (error propagation), §6.5 (range in scalar context), §6.6 (blank reference), §6.8 (function
// calls, #NAME?), §5.4 (#PARSE!), R9 (no depth-proportional recursion), R11, R12, P-4 (§12).
public class FormulaEvaluatorTests
{
    // ---- Literals and references ----

    [Fact]
    public void Literals()
    {
        Assert.Equal(N(1.5), Run("1.5"));
        Assert.Equal(T("hi \"x\""), Run("\"hi \"\"x\"\"\""));
        Assert.Equal(B(true), Run("true"));
        Assert.Equal(E(ErrorCodes.NA), Run("#n/a"));
    }

    [Fact]
    public void CellReference_ReturnsCellValue()
    {
        var cells = new DictionaryCells().Set("A1", 3).Set("B2", "txt").Set("C3", true).Set("D4", E(ErrorCodes.Ref));
        Assert.Equal(N(3), Run("A1", cells));
        Assert.Equal(N(3), Run("$A$1", cells));
        Assert.Equal(T("txt"), Run("b2", cells));
        Assert.Equal(B(true), Run("C3", cells));
        Assert.Equal(E(ErrorCodes.Ref), Run("D4", cells));
        Assert.Equal(N(6), Run("A1*2", cells));
    }

    [Fact]
    public void BlankReference_IsZero()
    {
        Assert.Equal(N(0), Run("A1"));
        Assert.Equal(N(0), Run("(A1)"));
        Assert.Equal(N(0), Run("((Z99))"));
        Assert.Equal(N(1), Run("A1+1"));
    }

    [Fact]
    public void BlankFunctionResult_IsZeroAtTopLevel_ButBlankInsideExpressions()
    {
        Assert.Equal(N(0), Run("BLANKFN()"));
        Assert.Equal(N(0), Run("ID(A1)"));
        Assert.Equal(T("x"), Run("ID(A1)&\"x\""));
        Assert.Equal(B(true), Run("ID(A1)=\"\""));
    }

    [Fact]
    public void EmptyTextCell_IsNotBlank() =>
        Assert.Equal(T(""), Run("A1", new DictionaryCells().Set("A1", "")));

    // ---- §6.5 / R11: ranges in scalar context ----

    [Theory]
    [InlineData("A1:B2")]
    [InlineData("A1:A1")]          // R11: 1x1 is still a range
    [InlineData("(A1:B2)")]
    [InlineData("A1:B2+1")]
    [InlineData("-A1:A1")]
    [InlineData("A1:A1&\"\"")]
    [InlineData("ID(A1:B2)")]
    [InlineData("ID((A1:A1))")]
    public void RangeInScalarContext_IsValue(string formula)
    {
        var cells = new DictionaryCells().Set("A1", 5);
        Assert.Equal(E(ErrorCodes.Value), Run(formula, cells));
    }

    // ---- §6.4 error propagation ----

    [Theory]
    [InlineData("#DIV/0!+#N/A", ErrorCodes.Div0)]
    [InlineData("#N/A+#DIV/0!", ErrorCodes.NA)]
    [InlineData("#N/A&#REF!", ErrorCodes.NA)]
    [InlineData("#REF!=#N/A", ErrorCodes.Ref)]
    [InlineData("1+#NUM!", ErrorCodes.Num)]
    [InlineData("#NUM!*0", ErrorCodes.Num)]
    [InlineData("-#N/A", ErrorCodes.NA)]
    [InlineData("#N/A%", ErrorCodes.NA)]
    [InlineData("(1/0)+#N/A", ErrorCodes.Div0)]
    [InlineData("#N/A+(1/0)", ErrorCodes.NA)]
    [InlineData("\"a\"+#N/A", ErrorCodes.Value)]     // left's coercion failure comes first
    [InlineData("#N/A+\"a\"", ErrorCodes.NA)]
    [InlineData("1+\"a\"*#N/A", ErrorCodes.Value)]
    [InlineData("(1/0)^0", ErrorCodes.Div0)]
    [InlineData("#N/A=#N/A", ErrorCodes.NA)]
    [InlineData("1<#N/A", ErrorCodes.NA)]
    [InlineData("A1:B2+#N/A", ErrorCodes.Value)]     // range error on the left wins
    [InlineData("#N/A+A1:B2", ErrorCodes.NA)]
    [InlineData("#CIRC!", ErrorCodes.Circ)]
    public void FirstErrorWins_LeftThenRight(string formula, string code) => Assert.Equal(E(code), Run(formula));

    [Fact]
    public void ErrorInReferencedCell_Propagates()
    {
        var cells = new DictionaryCells().Set("A1", E(ErrorCodes.NA)).Set("B1", E(ErrorCodes.Div0));
        Assert.Equal(E(ErrorCodes.NA), Run("A1+B1", cells));
        Assert.Equal(E(ErrorCodes.Div0), Run("B1&A1", cells));
        Assert.Equal(E(ErrorCodes.NA), Run("A1=B1", cells));
    }

    // ---- #NAME? and #PARSE! ----

    [Theory]
    [InlineData("foo")]
    [InlineData("foo+1")]
    [InlineData("A100001")]
    [InlineData("NOSUCH()")]
    [InlineData("NOSUCH(1,2)")]
    [InlineData("1+NOSUCH(1/0)")]
    [InlineData("ID(foo)")]
    public void UnknownNamesAndFunctions_AreName(string formula) => Assert.Equal(E(ErrorCodes.Name), Run(formula));

    [Fact]
    public void NoResolver_EveryFunctionIsName()
    {
        var parsed = FormulaParser.Parse("ID(1)", null);
        Assert.Equal(E(ErrorCodes.Name), FormulaEvaluator.Evaluate(parsed, new DictionaryCells(), null));
    }

    [Fact]
    public void FailedParse_IsParseError()
    {
        var parsed = FormulaParser.Parse("1+", null);
        Assert.False(parsed.Success);
        Assert.Equal(E(ErrorCodes.Parse), FormulaEvaluator.Evaluate(parsed, new DictionaryCells(), new FakeFunctions()));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var parsed = FormulaParser.Parse("1", null);
        Assert.Throws<ArgumentNullException>(() => FormulaEvaluator.Evaluate(null!, new DictionaryCells()));
        Assert.Throws<ArgumentNullException>(() => FormulaEvaluator.Evaluate(parsed, null!));
    }

    // ---- Function results ----

    [Fact]
    public void FunctionTextResult_OverMaxLength_IsValue()
    {
        Assert.Equal(E(ErrorCodes.Value), Run("BIGTEXT()"));
        Assert.Equal(32767, Run("MAXTEXT()").Text.Length);
    }

    [Fact]
    public void FunctionResults_FeedOperators()
    {
        var cells = new DictionaryCells().Set("A1", 2).Set("A2", 3);
        Assert.Equal(N(10), Run("FSUM(A1:A2)*2", cells));
        Assert.Equal(T("5!"), Run("FSUM(A1,A2)&\"!\"", cells));
    }

    // ---- R9: no depth-proportional recursion ----

    [Fact]
    public void LongLeftAssociativeChain_16000Operators_NoStackOverflow()
    {
        var sb = new StringBuilder("1");
        for (int i = 0; i < 16000; i++) sb.Append("+1");
        var text = sb.ToString();
        CellValue result = default;
        OnSmallStack(1024 * 1024, () => result = Run(text));
        Assert.Equal(N(16001), result);
    }

    [Fact]
    public void LongMixedChain_NoStackOverflow()
    {
        var sb = new StringBuilder("1");
        for (int i = 0; i < 8000; i++) sb.Append(i % 2 == 0 ? "*2" : "/2");
        sb.Append("&\"\"");
        CellValue result = default;
        OnSmallStack(1024 * 1024, () => result = Run(sb.ToString()));
        Assert.Equal(T("1"), result);
    }

    // R13: function calls nest at most 64 deep, so re-entry through lazy arguments is bounded. The fakes are ordinary
    // (not AggressiveOptimization) lambdas, as real library functions will be.
    [Fact]
    public void SixtyFourNestedSumLikeCalls_On1MbStack_NoStackOverflow()
    {
        var sum = string.Concat(Enumerable.Repeat("FSUM(", 64)) + "1" + string.Concat(Enumerable.Repeat(",1)", 64));
        var lazyIf = string.Concat(Enumerable.Repeat("FIF(TRUE,", 64)) + "7" + string.Concat(Enumerable.Repeat(",0)", 64));
        var withParens = string.Concat(Enumerable.Repeat("FSUM(" + new string('(', 15), 64)) + "1"
            + string.Concat(Enumerable.Repeat(new string(')', 15) + ",A1:A2)", 64)); // combined depth 1024 (R8 cap)
        var cells = new DictionaryCells().Set("A1", 1);
        CellValue s = default, f = default, p = default;
        OnSmallStack(1024 * 1024, () => { s = Run(sum); f = Run(lazyIf); p = Run(withParens, cells); });
        Assert.Equal(N(65), s);
        Assert.Equal(N(7), f);
        Assert.Equal(N(65), p);
    }

    [Fact]
    public void SixtyFiveNestedCalls_IsParseError()
    {
        var text = string.Concat(Enumerable.Repeat("FSUM(", 65)) + "1" + new string(')', 65);
        var parsed = FormulaParser.Parse(text, null);
        Assert.False(parsed.Success);
        Assert.Equal(E(ErrorCodes.Parse), FormulaEvaluator.Evaluate(parsed, new DictionaryCells(), new FakeFunctions()));
    }

    [Fact]
    public void ReentryWithInsufficientStack_IsNum_NotACrash()
    {
        // R13(b) backstop: a function that has already used up nearly all of the stack asks for an argument.
        var fns = new FakeFunctions();
        fns.Add("DEEP", a => Burn(a));
        CellValue result = default;
        OnSmallStack(1024 * 1024, () => result = Run("DEEP(1+1)", functions: fns));
        Assert.Equal(E(ErrorCodes.Num), result);

        // Non-tail recursion (the result is used after the call) so the JIT cannot turn Burn into a loop.
        static CellValue Burn(IFunctionArguments a)
        {
            if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack()) return a.Evaluate(0);
            var inner = Burn(a);
            GC.KeepAlive(a); // work after the call keeps it a non-tail call
            return inner;
        }
    }

    [Fact]
    public void DeepParensAndUnary_AtParserCap_NoStackOverflow()
    {
        var parens = new string('(', 1024) + "7" + new string(')', 1024);
        var unary = new string('-', 1024) + "7";
        CellValue p = default, u = default;
        OnSmallStack(1024 * 1024, () => { p = Run(parens); u = Run(unary); });
        Assert.Equal(N(7), p);
        Assert.Equal(N(7), u);
    }

    // ---- P-4: SUM over 100k cells without per-cell allocation ----

    [Fact]
    public void SumOver100kCells_IsFastAndDoesNotAllocatePerCell()
    {
        var cells = new DictionaryCells();
        for (int r = 1; r <= 100_000; r++) cells.Set(new CellAddress(1, r), CellValue.FromNumber(r));
        var parsed = FormulaParser.Parse("FSUM(A1:A100000)", null);
        var fns = new FakeFunctions();

        Assert.Equal(N(5000050000), FormulaEvaluator.Evaluate(parsed, cells, fns)); // warm-up

        long before = GC.GetAllocatedBytesForCurrentThread();
        var v = FormulaEvaluator.Evaluate(parsed, cells, fns);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(N(5000050000), v);
        Assert.True(allocated < 16 * 1024, $"allocated {allocated} bytes for 100k cells");
    }
}
