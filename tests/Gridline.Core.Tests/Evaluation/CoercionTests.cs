using Gridline.Core.Evaluation;
using static Gridline.Core.Tests.Evaluation.Eval;

namespace Gridline.Core.Tests.Evaluation;

// Requirement IDs: §6.2 (coercions), §4.3 (number grammar reused for text->number).
public class CoercionTests
{
    [Theory]
    [InlineData("5", 5)]
    [InlineData("  5  ", 5)]
    [InlineData(" -1.5e2 ", -150)]
    [InlineData("50%", 0.5)]
    [InlineData(".5", 0.5)]
    [InlineData("+3", 3)]
    public void TextToNumber_TrimsSpacesAndUsesNumberGrammar(string text, double expected)
    {
        Assert.Equal(N(expected), Coercion.ToNumber(T(text)));
        Assert.True(Coercion.TryToNumber(T(text), out var d, out _));
        Assert.Equal(expected, d);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("\t5")]          // only U+0020 is trimmed
    [InlineData("5 ")]      // NBSP is not trimmed
    [InlineData("1,000")]
    [InlineData("1e999")]        // non-finite is not a number
    [InlineData("TRUE")]
    [InlineData("5%%")]
    public void TextToNumber_Failure_IsValueError(string text)
    {
        Assert.Equal(E(ErrorCodes.Value), Coercion.ToNumber(T(text)));
        Assert.False(Coercion.TryToNumber(T(text), out _, out var err));
        Assert.Equal(E(ErrorCodes.Value), err);
    }

    [Fact]
    public void BooleanAndBlankToNumber()
    {
        Assert.Equal(N(1), Coercion.ToNumber(B(true)));
        Assert.Equal(N(0), Coercion.ToNumber(B(false)));
        Assert.Equal(N(0), Coercion.ToNumber(CellValue.Blank));
        Assert.Equal(N(7), Coercion.ToNumber(N(7)));
    }

    [Fact]
    public void Errors_PassThroughEveryCoercion()
    {
        var e = E(ErrorCodes.NA);
        Assert.Equal(e, Coercion.ToNumber(e));
        Assert.Equal(e, Coercion.ToText(e));
        Assert.Equal(e, Coercion.ToBoolean(e));
        Assert.False(Coercion.TryToNumber(e, out _, out var err));
        Assert.Equal(e, err);
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(1.5, "1.5")]
    [InlineData(-2.25, "-2.25")]
    [InlineData(0.1 + 0.2, "0.3")]                 // G15 hides binary noise
    [InlineData(123456789012345, "123456789012345")]
    [InlineData(1e15, "1E+15")]
    [InlineData(1.5e300, "1.5E+300")]
    [InlineData(1e-5, "1E-05")]                   // R14: exactly G15, exponent as .NET prints it
    [InlineData(1.25e-7, "1.25E-07")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(1e100, "1E+100")]
    [InlineData(0.0, "0")]
    public void NumberToText_UsesG15Invariant(double d, string expected)
    {
        Assert.Equal(expected, Coercion.NumberToText(d));
        Assert.Equal(T(expected), Coercion.ToText(CellValue.FromNumber(d)));
    }

    [Fact]
    public void NumberToText_NegativeZero_IsZero() => Assert.Equal("0", Coercion.NumberToText(-0.0));

    [Fact]
    public void NumberToText_IsCultureInvariant()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.5", Coercion.NumberToText(1.5));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void OtherKindsToText()
    {
        Assert.Equal(T("TRUE"), Coercion.ToText(B(true)));
        Assert.Equal(T("FALSE"), Coercion.ToText(B(false)));
        Assert.Equal(T(""), Coercion.ToText(CellValue.Blank));
        Assert.Equal(T(" a "), Coercion.ToText(T(" a ")));
    }

    [Theory]
    [InlineData("TRUE", true)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    public void TextToBoolean_CaseInsensitive(string text, bool expected) =>
        Assert.Equal(B(expected), Coercion.ToBoolean(T(text)));

    [Theory]
    [InlineData(" TRUE")]   // no trimming
    [InlineData("1")]       // numeric text is not boolean
    [InlineData("yes")]
    [InlineData("")]
    public void TextToBoolean_Other_IsValueError(string text) =>
        Assert.Equal(E(ErrorCodes.Value), Coercion.ToBoolean(T(text)));

    [Fact]
    public void NumberAndBlankToBoolean()
    {
        Assert.Equal(B(true), Coercion.ToBoolean(N(2)));
        Assert.Equal(B(true), Coercion.ToBoolean(N(-0.5)));
        Assert.Equal(B(false), Coercion.ToBoolean(N(0)));
        Assert.Equal(B(false), Coercion.ToBoolean(CellValue.Blank));
        Assert.Equal(B(true), Coercion.ToBoolean(B(true)));
    }
}
