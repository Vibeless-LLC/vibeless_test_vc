using Gridline.Core.Addressing;

namespace Gridline.Core.Tests.Addressing;

// Requirement IDs: CR1.1 (sheet name rules), CR1.2 (quoting a sheet prefix, '' for '), R82.
public class SheetNameTests
{
    [Theory]
    [InlineData("Sheet1")]
    [InlineData("a")]
    [InlineData("My Sheet")]
    [InlineData("O'Brien")]
    [InlineData("2024")]
    [InlineData("TRUE")]
    [InlineData("Été €")]
    [InlineData(" lead and trail ")]
    [InlineData("a!b")]
    [InlineData("1234567890123456789012345678901")] // 31 characters
    public void ValidNames_AreAccepted(string name)
    {
        Assert.True(SheetName.IsValid(name));
        SheetName.Validate(name, "name"); // does not throw
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345678901234567890123456789012")] // 32 characters
    [InlineData("a:b")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a[b")]
    [InlineData("a]b")]
    [InlineData(":")]
    [InlineData("'Quoted")]
    [InlineData("Quoted'")]
    [InlineData("'")]
    [InlineData("''")]
    public void InvalidNames_AreRejected_WithArgumentException(string name)
    {
        Assert.False(SheetName.IsValid(name));
        var ex = Assert.Throws<ArgumentException>(() => SheetName.Validate(name, "sheetName"));
        Assert.Equal("sheetName", ex.ParamName);
        Assert.Throws<ArgumentException>(() => SheetName.Format(name));
    }

    [Fact]
    public void Validate_ClipsALongRejectedNameInTheMessage()
    {
        var ex = Assert.Throws<ArgumentException>(() => SheetName.Validate(new string('x', 500), "sheetName"));
        Assert.Contains("'" + new string('x', 64) + "...'", ex.Message);
        Assert.DoesNotContain(new string('x', 65), ex.Message);
    }

    [Fact]
    public void LengthLimit_Is31()
    {
        Assert.Equal(31, SheetName.MaxLength);
        Assert.True(SheetName.IsValid(new string('x', 31)));
        Assert.False(SheetName.IsValid(new string('x', 32)));
    }

    [Fact]
    public void Null_IsInvalid_AndValidateThrowsArgumentNullException()
    {
        Assert.False(SheetName.IsValid(null));
        var ex = Assert.Throws<ArgumentNullException>(() => SheetName.Validate(null!, "sheetName"));
        Assert.Equal("sheetName", ex.ParamName);
        Assert.Throws<ArgumentNullException>(() => SheetName.Format(null!));
        Assert.Throws<ArgumentNullException>(() => SheetName.RequiresQuoting(null!));
    }

    // CR1.2: each quoting trigger, with the canonical prefix.
    [Theory]
    [InlineData("My Sheet", "'My Sheet'")]   // a character outside [A-Za-z0-9_.]
    [InlineData("a-b", "'a-b'")]
    [InlineData("Été", "'Été'")]
    [InlineData("a!b", "'a!b'")]
    [InlineData("2024", "'2024'")]           // starts with a digit
    [InlineData("1abc", "'1abc'")]
    [InlineData("A1", "'A1'")]               // looks like a cell reference
    [InlineData("abc123", "'abc123'")]
    [InlineData("ZZZ1", "'ZZZ1'")]           // outside the grid but matches ^[A-Za-z]{1,3}[0-9]+$
    [InlineData("A0", "'A0'")]
    [InlineData("TRUE", "'TRUE'")]           // a boolean in any casing
    [InlineData("true", "'true'")]
    [InlineData("False", "'False'")]
    [InlineData("O'Brien", "'O''Brien'")]    // '' stands for '
    [InlineData("a''b", "'a''''b'")]
    public void NamesNeedingQuotes_AreQuoted(string name, string formatted)
    {
        Assert.True(SheetName.RequiresQuoting(name));
        Assert.Equal(formatted, SheetName.Format(name));
    }

    [Theory]
    [InlineData("Data")]
    [InlineData("Sheet_1")]
    [InlineData("a.b")]
    [InlineData("Sheet1")]
    [InlineData("abcd1")]     // four letters: not cell-like
    [InlineData("A1B")]       // digits not at the end
    [InlineData("_x")]
    [InlineData(".5abc")]     // starts with '.', not a digit
    [InlineData("TRUEX")]
    public void NamesNeedingNoQuotes_PrintBare(string name)
    {
        Assert.False(SheetName.RequiresQuoting(name));
        Assert.Equal(name, SheetName.Format(name));
    }
}
