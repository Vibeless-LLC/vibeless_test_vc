using Gridline.App.Controls;
using Gridline.App.ViewModels;
using Gridline.Core;

namespace Gridline.App.Tests.ViewModels;

/// <summary>§11.3 display formatting: numbers G10 invariant right-aligned, text left, booleans and errors centred.</summary>
public class CellDisplayFormatterTests
{
    private static CellDisplay Display(string input) => CellDisplayFormatter.Format(Workbook.FromCells([new("A1", input)]).GetValue("A1"));

    [Theory]
    [InlineData("5", "5")]
    [InlineData("-2.5", "-2.5")]
    [InlineData("=1/3", "0.3333333333")]
    [InlineData("=0.1+0.2", "0.3")]
    [InlineData("12345678901", "1.23456789E+10")]
    [InlineData("1234567890", "1234567890")]
    [InlineData("0.0000001", "1E-07")]
    [InlineData("50%", "0.5")]
    [InlineData("-0", "0")]
    public void Numbers_G10Invariant_RightAligned(string input, string text)
    {
        Assert.Equal(new CellDisplay(text, CellAlignment.Right), Display(input));
    }

    [Fact]
    public void Numbers_IgnoreCurrentCulture()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.5", Display("1.5").Text);
        }
        finally { Thread.CurrentThread.CurrentCulture = saved; }
    }

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("'123", "123")]
    [InlineData(" 5", " 5")]
    [InlineData("=\"a\"&\"b\"", "ab")]
    public void Text_LeftAligned(string input, string text) =>
        Assert.Equal(new CellDisplay(text, CellAlignment.Left), Display(input));

    [Theory]
    [InlineData("TRUE", "TRUE")]
    [InlineData("false", "FALSE")]
    [InlineData("=1=1", "TRUE")]
    public void Booleans_Centered(string input, string text) =>
        Assert.Equal(new CellDisplay(text, CellAlignment.Center), Display(input));

    [Theory]
    [InlineData("=1/0", "#DIV/0!")]
    [InlineData("=NOPE()", "#NAME?")]
    [InlineData("=1+", "#PARSE!")]
    public void Errors_Centered(string input, string text) =>
        Assert.Equal(new CellDisplay(text, CellAlignment.Center), Display(input));

    [Fact]
    public void Blank_IsEmpty() => Assert.Equal(CellDisplay.Empty, CellDisplayFormatter.Format(CellValue.Blank));
}
