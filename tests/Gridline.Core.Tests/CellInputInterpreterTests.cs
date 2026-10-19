namespace Gridline.Core.Tests;

// Requirement IDs: §4.3 (cell input interpretation), §6.2 (number grammar reuse).
public class CellInputInterpreterTests
{
    [Theory]
    [InlineData("5", 5.0)]
    [InlineData("5%", 0.05)]
    [InlineData(".5", 0.5)]
    [InlineData("5.", 5.0)]
    [InlineData("1.5e3", 1500.0)]
    [InlineData("-.5", -0.5)]
    [InlineData("+5", 5.0)]
    [InlineData("1E2", 100.0)]
    [InlineData("1e-2", 0.01)]
    [InlineData("-50%", -0.5)]
    public void Numbers(string input, double expected)
    {
        var r = CellInputInterpreter.Interpret(input);
        Assert.Equal(CellInputKind.Number, r.Kind);
        Assert.Equal(CellValue.FromNumber(expected), r.Value);
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData("5%%")]
    [InlineData("1e")]
    [InlineData("e5")]
    [InlineData("TRUE ")]
    [InlineData(" TRUE")]
    [InlineData("abc")]
    [InlineData(".")]
    [InlineData("-")]
    [InlineData("%")]
    [InlineData("1,5")]
    [InlineData("١٢")] // non-ASCII digits
    public void Text(string input)
    {
        var r = CellInputInterpreter.Interpret(input);
        Assert.Equal(CellInputKind.Text, r.Kind);
        Assert.Equal(CellValue.FromText(input), r.Value);
    }

    [Theory]
    [InlineData("")]
    public void Empty_IsBlank(string input)
    {
        var r = CellInputInterpreter.Interpret(input);
        Assert.Equal(CellInputKind.Blank, r.Kind);
        Assert.Equal(CellValue.Blank, r.Value);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("False", false)]
    [InlineData("fAlSe", false)]
    public void Booleans(string input, bool expected)
    {
        var r = CellInputInterpreter.Interpret(input);
        Assert.Equal(CellInputKind.Boolean, r.Kind);
        Assert.Equal(CellValue.FromBoolean(expected), r.Value);
    }

    [Theory]
    [InlineData("=1+2", "1+2")]
    [InlineData("=", "")]
    [InlineData("= A1", " A1")]
    public void Formulas(string input, string formula)
    {
        var r = CellInputInterpreter.Interpret(input);
        Assert.Equal(CellInputKind.Formula, r.Kind);
        Assert.Equal(formula, r.FormulaText);
    }

    [Theory]
    [InlineData("'=x", "=x")]
    [InlineData("'5", "5")]
    [InlineData("'TRUE", "TRUE")]
    [InlineData("'", "")]
    [InlineData("''a", "'a")]
    public void LeadingApostrophe_IsTextWithoutIt(string input, string text)
    {
        var r = CellInputInterpreter.Interpret(input);
        Assert.Equal(CellInputKind.Text, r.Kind);
        Assert.Equal(CellValue.FromText(text), r.Value);
    }

    [Fact]
    public void Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => CellInputInterpreter.Interpret(null!));

    [Fact]
    public void MaxLength_Enforced()
    {
        Assert.Equal(32767, CellInputInterpreter.MaxInputLength);
        var ok = new string('a', 32767);
        Assert.Equal(CellInputKind.Text, CellInputInterpreter.Interpret(ok).Kind);
        Assert.Throws<ArgumentException>(() => CellInputInterpreter.Interpret(new string('a', 32768)));
    }

    [Fact]
    public void Culture_IsInvariant()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(CellValue.FromNumber(1.5), CellInputInterpreter.Interpret("1.5").Value);
            Assert.Equal(CellInputKind.Text, CellInputInterpreter.Interpret("1,5").Kind);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Theory]
    [InlineData("12", true, 12.0)]
    [InlineData("12%", true, 0.12)]
    [InlineData("1e999", false, 0.0)]
    [InlineData("", false, 0.0)]
    [InlineData("1e", false, 0.0)]
    public void NumberGrammar_TryParse(string s, bool ok, double expected)
    {
        Assert.Equal(ok, NumberGrammar.TryParse(s, out var v));
        if (ok) Assert.Equal(expected, v);
    }
}
