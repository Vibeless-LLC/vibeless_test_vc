using Gridline.Core.Evaluation;
using static Gridline.Core.Tests.Evaluation.Eval;

namespace Gridline.Core.Tests.Evaluation;

// Requirement IDs: §6.3 (operators), §5.2 (precedence as evaluated), §6.2 (operand coercion).
public class OperatorTests
{
    // ---- Acceptance items (task 7 brief) ----

    [Fact] public void NegativeTwoSquared_IsFour() => Assert.Equal(N(4), Run("-2^2"));
    [Fact] public void PowerIsLeftAssociative_2_3_2_Is64() => Assert.Equal(N(64), Run("2^3^2"));
    [Fact] public void AddBindsTighterThanConcat_1Plus2Amp3_Is33() => Assert.Equal(T("33"), Run("1+2&3"));
    [Fact] public void ConcatThenCompare_aAmp1Eq1_IsFalse() => Assert.Equal(B(false), Run("\"a\"&1=1"));
    [Fact] public void DoublePercent_50PctPct_Is0005() => Assert.Equal(N(0.005), Run("50%%"));

    // ---- Arithmetic ----

    [Theory]
    [InlineData("1+2", 3)]
    [InlineData("5-7", -2)]
    [InlineData("3*4", 12)]
    [InlineData("7/2", 3.5)]
    [InlineData("2^10", 1024)]
    [InlineData("2^-2", 0.25)]
    [InlineData("(-8)^3", -512)]
    [InlineData("-8^(1/1)", -8)]
    [InlineData("1+2*3", 7)]
    [InlineData("(1+2)*3", 9)]
    [InlineData("-(-3)", 3)]
    [InlineData("--3", 3)]
    [InlineData("+-+3", -3)]
    [InlineData("50%", 0.5)]
    [InlineData("-50%", -0.5)]
    [InlineData("200%*3", 6)]
    [InlineData("\" 2 \"*\"3\"", 6)]
    [InlineData("\"1e2\"+0", 100)]
    [InlineData("\"50%\"*2", 1)]
    [InlineData("TRUE+TRUE", 2)]
    [InlineData("FALSE*5", 0)]
    [InlineData("-TRUE", -1)]
    [InlineData("\"10\"%", 0.1)]
    public void Arithmetic(string formula, double expected) => Assert.Equal(N(expected), Run(formula));

    [Theory]
    [InlineData("\"a\"+1")]
    [InlineData("1-\"\"")]
    [InlineData("-\"x\"")]
    [InlineData("\"x\"%")]
    [InlineData("\"TRUE\"*1")]
    public void Arithmetic_NonNumericText_IsValue(string formula) => Assert.Equal(E(ErrorCodes.Value), Run(formula));

    // R15: unary + is a no-op (no coercion); a blank operand still yields 0 (§6.6).
    [Fact]
    public void UnaryPlus_ReturnsOperandUnchanged()
    {
        var cells = new DictionaryCells().Set("B1", "t");
        Assert.Equal(T("x"), Run("+\"x\""));
        Assert.Equal(T("4"), Run("+\"4\""));
        Assert.Equal(B(true), Run("+TRUE"));
        Assert.Equal(N(3), Run("+3"));
        Assert.Equal(E(ErrorCodes.NA), Run("+#N/A"));
        Assert.Equal(T("t"), Run("+B1", cells));
        Assert.Equal(T("ab"), Run("+\"a\"&\"b\""));
        Assert.Equal(B(false), Run("+\"1\"=1"));  // still text, so not equal to a number
        Assert.Equal(N(0), Run("+A1"));
        Assert.Equal(T("0x"), Run("(+A1)&\"x\""));
        Assert.Equal(N(-4), Run("-+\"4\""));      // negation still coerces
        Assert.Equal(E(ErrorCodes.Value), Run("+A1:A2"));
    }

    [Fact]
    public void ArithmeticResult_NegativeZero_IsZero()
    {
        var v = Run("-0*1");
        Assert.Equal(N(0), v);
        Assert.False(double.IsNegative(v.Number));
        Assert.False(double.IsNegative(Run("0*-1").Number));
    }

    [Theory]
    [InlineData("1/0")]
    [InlineData("0/0")]
    [InlineData("1/(2-2)")]
    [InlineData("5/A1")]       // blank divisor is 0
    [InlineData("5/FALSE")]
    public void DivisionByZero(string formula) => Assert.Equal(E(ErrorCodes.Div0), Run(formula));

    [Theory]
    [InlineData("1E308*10")]
    [InlineData("-1E308*10")]
    [InlineData("10^400")]
    [InlineData("1E308+1E308")]
    public void NonFiniteResult_IsNum(string formula) => Assert.Equal(E(ErrorCodes.Num), Run(formula));

    // ---- Power edge cases ----

    [Theory]
    [InlineData("0^0", ErrorCodes.Num)]
    [InlineData("0^-1", ErrorCodes.Div0)]
    [InlineData("0^-0.5", ErrorCodes.Div0)]
    [InlineData("(-8)^(1/3)", ErrorCodes.Num)]
    [InlineData("(-2)^0.5", ErrorCodes.Num)]
    [InlineData("A1^A1", ErrorCodes.Num)]       // blank^blank = 0^0
    public void Power_EdgeCases(string formula, string code) => Assert.Equal(E(code), Run(formula));

    [Theory]
    [InlineData("0^2", 0)]
    [InlineData("5^0", 1)]
    [InlineData("(-2)^2", 4)]
    [InlineData("(-2)^3", -8)]
    [InlineData("(-2)^-1", -0.5)]
    [InlineData("4^0.5", 2)]
    public void Power_Normal(string formula, double expected) => Assert.Equal(N(expected), Run(formula));

    // ---- Concatenation ----

    [Theory]
    [InlineData("\"a\"&\"b\"", "ab")]
    [InlineData("1&2", "12")]
    [InlineData("1.5&\"x\"", "1.5x")]
    [InlineData("TRUE&FALSE", "TRUEFALSE")]
    [InlineData("A1&\"x\"", "x")]              // blank concatenates as ""
    [InlineData("(0.1+0.2)&\"\"", "0.3")]
    [InlineData("1E-5&\"\"", "1E-05")]
    [InlineData("\"\"&\"\"", "")]
    public void Concat(string formula, string expected) => Assert.Equal(T(expected), Run(formula));

    [Fact]
    public void Concat_ExactlyMaxLength_IsAllowed_LongerIsValue()
    {
        var fns = new FakeFunctions();
        Assert.Equal(32767, Run("MAXTEXT()&\"\"", functions: fns).Text.Length);
        Assert.Equal(E(ErrorCodes.Value), Run("MAXTEXT()&\"y\"", functions: fns));
        Assert.Equal(E(ErrorCodes.Value), Run("\"y\"&MAXTEXT()", functions: fns));
    }

    // ---- Comparison ----

    [Theory]
    [InlineData("1=1", true)]
    [InlineData("1<>1", false)]
    [InlineData("1<2", true)]
    [InlineData("2<=2", true)]
    [InlineData("3>2", true)]
    [InlineData("2>=3", false)]
    [InlineData("\"abc\"=\"ABC\"", true)]
    [InlineData("\"a\"<\"B\"", true)]
    [InlineData("\"B\"<\"a\"", false)]
    [InlineData("\"a\"<\"ab\"", true)]
    [InlineData("\"\"<\"a\"", true)]
    [InlineData("FALSE<TRUE", true)]
    [InlineData("TRUE=TRUE", true)]
    [InlineData("\"straße\"=\"STRASSE\"", false)]   // ToUpperInvariant is not full case folding
    [InlineData("\"é\"=\"É\"", true)]
    public void Comparison_SameType(string formula, bool expected) => Assert.Equal(B(expected), Run(formula));

    [Theory]
    // Number < Text < Boolean
    [InlineData("1<\"a\"", true)]
    [InlineData("999999<\"\"", true)]
    [InlineData("\"1\"=1", false)]     // no coercion in comparisons
    [InlineData("\"1\">1", true)]
    [InlineData("\"zzz\"<TRUE", true)]
    [InlineData("\"TRUE\"=TRUE", false)]
    [InlineData("1<FALSE", true)]
    [InlineData("1=TRUE", false)]
    [InlineData("TRUE>\"a\"", true)]
    [InlineData("FALSE>99", true)]
    public void Comparison_CrossType_NumberTextBoolean(string formula, bool expected) =>
        Assert.Equal(B(expected), Run(formula));

    [Theory]
    // A1 and A2 are blank: blank adapts to the other side's type
    [InlineData("A1=0", true)]
    [InlineData("0=A1", true)]
    [InlineData("A1<1", true)]
    [InlineData("A1>-1", true)]
    [InlineData("A1=\"\"", true)]
    [InlineData("\"\"=A1", true)]
    [InlineData("A1<\"a\"", true)]
    [InlineData("A1=FALSE", true)]
    [InlineData("A1<TRUE", true)]
    [InlineData("A1=A2", true)]
    [InlineData("A1<>A2", false)]
    [InlineData("A1<A2", false)]
    [InlineData("A1<=A2", true)]
    [InlineData("A1=1", false)]
    [InlineData("A1=\" \"", false)]
    public void Comparison_Blank_AdaptsToOtherSide(string formula, bool expected) =>
        Assert.Equal(B(expected), Run(formula));

    [Fact]
    public void ValueComparer_Ordering()
    {
        Assert.True(ValueComparer.Compare(N(5), T("")) < 0);
        Assert.True(ValueComparer.Compare(T("z"), B(false)) < 0);
        Assert.True(ValueComparer.Compare(B(true), N(1)) > 0);
        Assert.Equal(0, ValueComparer.Compare(T("Hello"), T("hELLO")));
        Assert.Equal(0, ValueComparer.Compare(CellValue.Blank, N(0)));
        Assert.Equal(0, ValueComparer.Compare(CellValue.Blank, CellValue.Blank));
        Assert.True(ValueComparer.Compare(CellValue.Blank, N(-1)) > 0);
    }

    [Fact]
    public void ValueComparer_Errors_Throw() =>
        Assert.Throws<ArgumentException>(() => ValueComparer.Compare(E(ErrorCodes.NA), N(1)));

    [Fact]
    public void Comparison_ChainsLeftToRight() => Assert.Equal(B(true), Run("1=2=FALSE"));

    // §6.3, §6.10.4: equality is exact IEEE, with no 15-digit tolerance.
    [Fact] public void Equality_IsExactIeee_PointOnePlusPointTwo_IsNotPointThree() => Assert.Equal(B(false), Run("0.1+0.2=0.3"));
}
