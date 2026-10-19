using Gridline.Core.Addressing;
using Gridline.Core.Evaluation;
using Gridline.Core.Functions;
using Gridline.Core.Parsing;
using Gridline.Core.Tests.Evaluation;
using static Gridline.Core.Tests.Functions.Fx;

namespace Gridline.Core.Tests.Functions;

// Requirement IDs: §7.0 (aggregation table), §7.1 (math functions), §6.9 (integer truncation), R16, R17.
public class MathFunctionTests
{
    // A1=1, A2="text", A3=TRUE, A4=4, A5 blank.
    private static DictionaryCells Mixed() => new DictionaryCells()
        .Set("A1", 1).Set("A2", "text").Set("A3", true).Set("A4", 4);

    // ---- Acceptance cases from the brief ----

    [Theory]
    [InlineData("ROUND(2.675,2)", 2.68)]
    [InlineData("ROUND(0.285,2)", 0.29)]
    [InlineData("ROUND(-2.5,0)", -3)]
    [InlineData("ROUND(1250,-2)", 1300)]
    [InlineData("MOD(-3,2)", 1)]
    [InlineData("MOD(3,-2)", -1)]
    [InlineData("INT(-2.5)", -3)]
    public void AcceptanceCases(string formula, double expected) => Assert.Equal(N(expected), Run(formula));

    [Fact]
    public void EmptyAverage_IsDivZero()
    {
        Assert.Equal(E(ErrorCodes.DivZero), Run("AVERAGE(A1:A3)"));
        Assert.Equal(E(ErrorCodes.DivZero), Run("AVERAGE(A1:A3,B1)"));
    }

    // ---- ROUND family ----

    [Theory]
    [InlineData("ROUND(2.5,0)", 3)]
    [InlineData("ROUND(1.005,2)", 1.01)]
    [InlineData("ROUND(-1.005,2)", -1.01)]
    [InlineData("ROUND(2.4999,0)", 2)]
    [InlineData("ROUND(1234.5678,-2)", 1200)]
    [InlineData("ROUND(-1250,-2)", -1300)]
    [InlineData("ROUND(149,-2)", 100)]
    [InlineData("ROUND(0.4,0)", 0)]
    [InlineData("ROUND(-0.4,0)", 0)]
    [InlineData("ROUND(5,-2)", 0)]
    [InlineData("ROUND(50,-2)", 100)]
    [InlineData("ROUND(1E-300,2)", 0)]
    [InlineData("ROUND(1.5E+300,2)", 1.5E+300)]
    [InlineData("ROUND(1.5E+300,-299)", 1.5E+300)]
    [InlineData("ROUND(1.25E+30,-29)", 1.3E+30)]
    [InlineData("ROUND(2.5,500)", 2.5)]
    [InlineData("ROUND(2.5,-500)", 0)]
    [InlineData("ROUND(123456789012345678,-3)", 123456789012346000)]
    [InlineData("ROUNDUP(2.1,0)", 3)]
    [InlineData("ROUNDUP(-2.1,0)", -3)]
    [InlineData("ROUNDUP(2.0001,2)", 2.01)]
    [InlineData("ROUNDUP(2,2)", 2)]
    [InlineData("ROUNDUP(1201,-2)", 1300)]
    [InlineData("ROUNDUP(1E-300,2)", 0.01)]
    [InlineData("ROUNDDOWN(2.9,0)", 2)]
    [InlineData("ROUNDDOWN(-2.9,0)", -2)]
    [InlineData("ROUNDDOWN(2.679,2)", 2.67)]
    [InlineData("ROUNDDOWN(1299,-2)", 1200)]
    [InlineData("ROUNDDOWN(0.1+0.2,1)", 0.3)]
    public void RoundFamily(string formula, double expected) => Assert.Equal(N(expected), Run(formula));

    [Theory]
    [InlineData("ROUND(2.675,2.9)", 2.68)]   // §6.9: digits 2.9 truncates to 2
    [InlineData("ROUND(1250,-2.9)", 1300)]   // -2.9 truncates toward zero to -2
    [InlineData("ROUND(2.5,0.9)", 3)]        // 0.9 -> 0
    [InlineData("ROUNDUP(2.111,1.9)", 2.2)]
    [InlineData("ROUNDDOWN(2.999,1.9)", 2.9)]
    [InlineData("ROUND(2.675,\"2\")", 2.68)]
    [InlineData("ROUND(2.675,TRUE)", 2.7)]
    [InlineData("ROUND(\"2.5\",0)", 3)]
    public void RoundDigits_TruncateTowardZero(string formula, double expected) => Assert.Equal(N(expected), Run(formula));

    [Fact]
    public void RoundOverflow_IsNum() => Assert.Equal(E(ErrorCodes.Num), Run("ROUNDUP(1.5E+308,-308)"));

    [Fact]
    public void Round_Errors()
    {
        Assert.Equal(E(ErrorCodes.Value), Run("ROUND(\"x\",1)"));
        Assert.Equal(E(ErrorCodes.NA), Run("ROUND(#N/A,#DIV/0!)"));
        Assert.Equal(E(ErrorCodes.DivZero), Run("ROUND(1,#DIV/0!)"));
    }

    // ---- SUM / aggregation ----

    [Fact]
    public void Sum_ReferenceIgnoresTextBooleanBlank() => Assert.Equal(N(5), Run("SUM(A1:A5)", Mixed()));

    [Fact]
    public void Sum_ReferenceWithError_Propagates() =>
        Assert.Equal(E(ErrorCodes.Ref), Run("SUM(A1:A5)", Mixed().Set("A5", E(ErrorCodes.Ref))));

    [Fact]
    public void Sum_FirstErrorWinsInArgumentOrder()
    {
        var cells = new DictionaryCells().Set("A1", E(ErrorCodes.NA)).Set("B1", E(ErrorCodes.Ref));
        Assert.Equal(E(ErrorCodes.NA), Run("SUM(A1,B1)", cells));
        Assert.Equal(E(ErrorCodes.Ref), Run("SUM(B1,A1)", cells));
    }

    [Fact]
    public void Sum_DirectArguments()
    {
        Assert.Equal(N(6), Run("SUM(\"5\",TRUE)"));
        Assert.Equal(E(ErrorCodes.Value), Run("SUM(\"x\")"));
        Assert.Equal(E(ErrorCodes.Value), Run("SUM(1,\"x\")"));
        Assert.Equal(E(ErrorCodes.DivZero), Run("SUM(1,1/0)"));
        Assert.Equal(N(3), Run("SUM(1,,2)"));
        Assert.Equal(N(0.5), Run("SUM(\"50%\")"));
    }

    [Fact]
    public void Sum_ParenthesisedReferenceStaysReference_R12() =>
        Assert.Equal(N(0), Run("SUM((A2))", new DictionaryCells().Set("A2", "text")));

    [Fact]
    public void Sum_SingleCellReference_IgnoresText()
    {
        var cells = new DictionaryCells().Set("A1", "7").Set("B1", 7);
        Assert.Equal(N(0), Run("SUM(A1)", cells));
        Assert.Equal(N(7), Run("SUM(B1)", cells));
    }

    [Fact]
    public void Sum_Overflow_IsNum() => Assert.Equal(E(ErrorCodes.Num), Run("SUM(1E+308,1E+308)"));

    [Fact]
    public void Sum_MixedReferenceAndDirect() => Assert.Equal(N(15), Run("SUM(A1:A5,\"10\")", Mixed()));

    [Fact]
    public void Sum_100kRange_DoesNotAllocatePerCell()
    {
        var cells = new DictionaryCells();
        for (int r = 1; r <= 100_000; r++) cells.Set(new CellAddress(1, r), CellValue.FromNumber(1));
        var parsed = FormulaParser.Parse("SUM(A1:A100000)", new FunctionArityAdapter());
        var table = BuiltInFunctionTable.Default;
        Assert.Equal(N(100_000), FormulaEvaluator.Evaluate(parsed, cells, table)); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = FormulaEvaluator.Evaluate(parsed, cells, table);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(N(100_000), result);
        Assert.True(allocated < 16 * 1024, $"allocated {allocated} bytes");
    }

    [Fact]
    public void Average()
    {
        Assert.Equal(N(2.5), Run("AVERAGE(A1:A5)", Mixed()));
        Assert.Equal(N(7.0 / 3), Run("AVERAGE(1,\"5\",TRUE,A2)", Mixed()));  // direct 1, 5, TRUE=1; reference A2 (text) ignored
        Assert.Equal(E(ErrorCodes.Value), Run("AVERAGE(\"x\")"));
        Assert.Equal(E(ErrorCodes.DivZero), Run("AVERAGE(A2:A3)", Mixed()));
    }

    [Fact]
    public void MinMax()
    {
        Assert.Equal(N(1), Run("MIN(A1:A5)", Mixed()));
        Assert.Equal(N(4), Run("MAX(A1:A5)", Mixed()));
        Assert.Equal(N(-2), Run("MIN(3,-2,\"9\")"));
        Assert.Equal(N(1), Run("MAX(TRUE,\"-5\")"));
        Assert.Equal(N(0), Run("MIN(A2:A3)", Mixed()));          // R17: no numbers -> 0
        Assert.Equal(N(0), Run("MAX(A9:A10)"));
        Assert.Equal(N(-3), Run("MAX(-3,A9:A10)"));              // blank ignored in a reference
        Assert.Equal(E(ErrorCodes.Value), Run("MAX(\"x\")"));
        Assert.Equal(E(ErrorCodes.Num), Run("MAX(1,A1)", new DictionaryCells().Set("A1", E(ErrorCodes.Num))));
    }

    [Fact]
    public void Product()
    {
        Assert.Equal(N(24), Run("PRODUCT(2,3,4)"));
        Assert.Equal(N(4), Run("PRODUCT(A1:A5)", Mixed()));
        Assert.Equal(N(0), Run("PRODUCT(A2:A3)", Mixed()));      // R17: no numbers -> 0
        Assert.Equal(N(6), Run("PRODUCT(\"2\",TRUE,3)"));
        Assert.Equal(E(ErrorCodes.Value), Run("PRODUCT(\"x\")"));
    }

    [Fact]
    public void SumSq()
    {
        Assert.Equal(N(25), Run("SUMSQ(3,4)"));
        Assert.Equal(N(17), Run("SUMSQ(A1:A5)", Mixed()));
        Assert.Equal(N(0), Run("SUMSQ(A2:A3)", Mixed()));
        Assert.Equal(E(ErrorCodes.Value), Run("SUMSQ(\"x\")"));
    }

    // ---- COUNT / COUNTA / COUNTBLANK (R17) ----

    [Fact]
    public void Count()
    {
        Assert.Equal(N(2), Run("COUNT(A1:A5)", Mixed()));                 // refs: numbers only
        Assert.Equal(N(3), Run("COUNT(1,TRUE,\"5\")"));                    // direct: numbers, booleans, numeric text
        Assert.Equal(N(0), Run("COUNT(\"x\",#N/A)"));                       // direct text / error: not counted, not propagated
        Assert.Equal(N(0), Run("COUNT(A1:A2)", new DictionaryCells().Set("A1", E(ErrorCodes.Ref))));
        Assert.Equal(N(2), Run("COUNT(1,,2)"));                             // omitted not counted
    }

    [Fact]
    public void CountA()
    {
        var cells = Mixed().Set("A5", E(ErrorCodes.Ref)).Set("A6", "");
        Assert.Equal(N(7), Run("COUNTA(A1:A7)", cells.Set("A7", 0)));      // everything non-blank incl. errors and ""
        Assert.Equal(N(0), Run("COUNTA(A9:A10)"));
        Assert.Equal(N(3), Run("COUNTA(1,\"\",#N/A)"));                    // direct: every present value incl. errors and ""
        Assert.Equal(N(2), Run("COUNTA(1,,2)"));
    }

    [Fact]
    public void CountBlank()
    {
        var cells = Mixed().Set("A6", "");                                 // A5 truly blank, A6 is text ""
        Assert.Equal(N(2), Run("COUNTBLANK(A1:A6)", cells));
        Assert.Equal(N(1), Run("COUNTBLANK(A9)"));
        Assert.Equal(N(0), Run("COUNTBLANK(A1:A4)", cells));
        Assert.Equal(N(0), Run("COUNTBLANK(A1)", new DictionaryCells().Set("A1", E(ErrorCodes.Ref))));
        Assert.Equal(E(ErrorCodes.Value), Run("COUNTBLANK(5)"));          // references only
    }

    // ---- SUMPRODUCT ----

    [Fact]
    public void SumProduct()
    {
        var cells = new DictionaryCells().Set("A1", 1).Set("A2", 2).Set("A3", 3).Set("B1", 4).Set("B2", 5).Set("B3", 6);
        Assert.Equal(N(32), Run("SUMPRODUCT(A1:A3,B1:B3)", cells));
        Assert.Equal(N(6), Run("SUMPRODUCT(A1:A3)", cells));
        Assert.Equal(N(78), Run("SUMPRODUCT(A1:A3,B1:B3,A1:A3)", cells));
    }

    [Fact]
    public void SumProduct_TwoDimensional()
    {
        var cells = new DictionaryCells().Set("A1", 1).Set("B1", 2).Set("A2", 3).Set("B2", 4);
        Assert.Equal(N(30), Run("SUMPRODUCT(A1:B2,A1:B2)", cells));
    }

    [Fact]
    public void SumProduct_NonNumericAreZero_ErrorsPropagate()
    {
        var cells = new DictionaryCells().Set("A1", 2).Set("A2", "x").Set("A3", true).Set("B1", 3).Set("B2", 4).Set("B3", 5);
        Assert.Equal(N(6), Run("SUMPRODUCT(A1:A3,B1:B3)", cells));
        cells.Set("B3", E(ErrorCodes.NA));
        Assert.Equal(E(ErrorCodes.NA), Run("SUMPRODUCT(A1:A3,B1:B3)", cells));
    }

    [Fact]
    public void SumProduct_DimensionMismatch_IsValue()
    {
        Assert.Equal(E(ErrorCodes.Value), Run("SUMPRODUCT(A1:A3,B1:B2)"));
        Assert.Equal(E(ErrorCodes.Value), Run("SUMPRODUCT(A1:A4,B1:D1)"));   // same cell count, different shape
        Assert.Equal(E(ErrorCodes.Value), Run("SUMPRODUCT(A1:A3,2)"));
    }

    [Fact]
    public void SumProduct_DirectScalars()
    {
        Assert.Equal(N(6), Run("SUMPRODUCT(2,3)"));
        Assert.Equal(N(0), Run("SUMPRODUCT(2,\"x\")"));
        Assert.Equal(E(ErrorCodes.DivZero), Run("SUMPRODUCT(2,1/0)"));
    }

    // ---- Scalar functions ----

    [Theory]
    [InlineData("ABS(-3.5)", 3.5)]
    [InlineData("ABS(\"-2\")", 2)]
    [InlineData("ABS(TRUE)", 1)]
    [InlineData("ABS(A1)", 0)]
    [InlineData("SIGN(-9)", -1)]
    [InlineData("SIGN(0)", 0)]
    [InlineData("SIGN(0.001)", 1)]
    [InlineData("INT(2.9)", 2)]
    [InlineData("INT(-2.1)", -3)]
    [InlineData("INT(-2)", -2)]
    [InlineData("INT(\"7.5\")", 7)]
    [InlineData("MOD(10,3)", 1)]
    [InlineData("MOD(-3,-2)", -1)]
    [InlineData("MOD(-10,3)", 2)]
    [InlineData("MOD(5.5,2)", 1.5)]
    [InlineData("MOD(6,3)", 0)]
    [InlineData("MOD(-6,3)", 0)]
    [InlineData("POWER(2,10)", 1024)]
    [InlineData("POWER(4,0.5)", 2)]
    [InlineData("POWER(2,-1)", 0.5)]
    [InlineData("POWER(-2,3)", -8)]
    [InlineData("SQRT(16)", 4)]
    [InlineData("SQRT(0)", 0)]
    [InlineData("PI()", Math.PI)]
    public void ScalarFunctions(string formula, double expected) => Assert.Equal(N(expected), Run(formula));

    [Theory]
    [InlineData("SQRT(-1)", ErrorCodes.Num)]
    [InlineData("MOD(1,0)", ErrorCodes.DivZero)]
    [InlineData("MOD(0,0)", ErrorCodes.DivZero)]
    [InlineData("POWER(0,0)", ErrorCodes.Num)]
    [InlineData("POWER(0,-1)", ErrorCodes.DivZero)]
    [InlineData("POWER(-8,0.5)", ErrorCodes.Num)]
    [InlineData("POWER(10,400)", ErrorCodes.Num)]
    [InlineData("ABS(\"x\")", ErrorCodes.Value)]
    [InlineData("ABS(#N/A)", ErrorCodes.NA)]
    [InlineData("SQRT(\"x\")", ErrorCodes.Value)]
    [InlineData("MOD(\"x\",#N/A)", ErrorCodes.Value)]
    [InlineData("MOD(1,#N/A)", ErrorCodes.NA)]
    [InlineData("SIGN(A1:A2)", ErrorCodes.Value)]
    public void ScalarFunctionErrors(string formula, string code) => Assert.Equal(E(code), Run(formula));

    [Fact]
    public void FunctionNames_AreCaseInsensitive() => Assert.Equal(N(4), Run("sqrt(16)"));

    [Fact]
    public void Nesting() => Assert.Equal(N(10), Run("SUM(ABS(-4),ROUND(SQRT(36),0),MIN(0,A1))"));
}
