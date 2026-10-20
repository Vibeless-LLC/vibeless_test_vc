using Gridline.Core;
using Gridline.Core.Addressing;

namespace Gridline.IO.Tests;

/// <summary>§8.5 record and field rules (<see cref="DelimitedTextParser"/>) and the field-to-input mapping
/// (<see cref="DelimitedTextMapping"/>) shared by clipboard paste (§11.6) and CSV import.</summary>
public class DelimitedTextTests
{
    private static string[][] P(string text, char delimiter = '\t') =>
        DelimitedTextParser.Parse(text, delimiter).Select(r => r.ToArray()).ToArray();

    [Fact]
    public void EmptyText_HasNoRecords() => Assert.Empty(P(""));

    [Fact]
    public void Records_SeparatedByCrLfLfOrCr_InAnyMix()
    {
        Assert.Equal([["a", "b"], ["c"], ["d"], ["e", "f"], ["g"]], P("a\tb\r\nc\nd\re\tf\r\ng"));
    }

    [Theory]
    [InlineData("a\tb\r\n")]
    [InlineData("a\tb\n")]
    [InlineData("a\tb\r")]
    public void FinalTerminator_DoesNotCreateAnEmptyRow(string text) => Assert.Equal([["a", "b"]], P(text));

    [Fact]
    public void BlankLines_InsideTheText_AreRecordsWithOneEmptyField()
    {
        Assert.Equal([["a"], [""], ["b"]], P("a\r\n\r\nb\r\n"));
        Assert.Equal([["a"], [""]], P("a\n\n")); // only the final terminator is absorbed
        Assert.Equal([[""]], P("\r\n"));
    }

    [Fact]
    public void EmptyFields_AreKept()
    {
        Assert.Equal([["", "x", ""]], P("\tx\t"));
        Assert.Equal([["", ""]], P("\t\n"));
    }

    [Fact]
    public void QuotedField_MayContainTheDelimiter()
    {
        Assert.Equal([["a\tb", "c"]], P("\"a\tb\"\tc"));
        Assert.Equal([["a,b", "c"]], P("\"a,b\",c", ','));
    }

    [Fact]
    public void QuotedField_DoubledQuoteIsOneQuote()
    {
        Assert.Equal([["say \"hi\"", "x"]], P("\"say \"\"hi\"\"\"\tx"));
        Assert.Equal([["\""]], P("\"\"\"\""));
        Assert.Equal([["", "y"]], P("\"\"\ty")); // an empty quoted field
    }

    [Fact]
    public void QuotedField_MayContainLineBreaks_KeptAsWritten()
    {
        Assert.Equal([["line1\r\nline2", "b"], ["c"]], P("\"line1\r\nline2\"\tb\r\nc"));
        Assert.Equal([["x\ny\rz"]], P("\"x\ny\rz\"\n"));
    }

    [Fact]
    public void QuoteInsideAnUnquotedField_IsLiteral()
    {
        Assert.Equal([["5\"", "a\"b\"c"]], P("5\"\ta\"b\"c"));
        Assert.Equal([[" \"x\""]], P(" \"x\"")); // a leading space makes the field unquoted
    }

    [Fact]
    public void TextAfterTheClosingQuote_IsAppendedLiterally()
    {
        Assert.Equal([["ab c", "d"]], P("\"ab\" c\td"));
    }

    [Theory]
    [InlineData("\"abc")]
    [InlineData("x\t\"abc\r\n")]
    [InlineData("\"a\"\"")]
    [InlineData("a\n\"")]
    public void UnterminatedQuotedField_AtEof_IsAFormatError(string text)
    {
        var ex = Assert.Throws<GridlineFormatException>(() => DelimitedTextParser.Parse(text, '\t'));
        Assert.Contains("not terminated", ex.Message);
    }

    [Theory]
    [InlineData('"')]
    [InlineData('\r')]
    [InlineData('\n')]
    public void InvalidDelimiter_IsAnArgumentError(char delimiter)
    {
        Assert.Throws<ArgumentException>(() => DelimitedTextParser.Parse("a", delimiter));
    }

    [Fact]
    public void Delimiter_IsAParameter()
    {
        Assert.Equal([["a\tb", "c"]], P("a\tb;c", ';'));
        Assert.Equal([["a", "b"]], P("a,b", ','));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("=A1+1", "'=A1+1")]
    [InlineData("'quoted", "''quoted")]
    [InlineData("-5", "-5")]
    [InlineData("5%", "5%")]
    [InlineData("true", "true")]
    [InlineData(" =x", " =x")]
    [InlineData("hello", "hello")]
    public void Mapping_EmptyIsBlank_EqualsAndApostropheArePrefixed_OthersVerbatim(string field, string input)
    {
        Assert.Equal(input, DelimitedTextMapping.ToCellInput(field));
    }

    [Fact]
    public void Mapping_InterpretsThroughTheWorkbook()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "keep");
        var records = DelimitedTextParser.Parse("-5\t5%\ttrue\r\n=A1\t'x\t\r\n", '\t');
        wb.ApplyBatch(DelimitedTextMapping.ToEdits(records, CellAddress.Parse("A1")));
        Assert.Equal("N:-5", wb.GetValue("A1").ToCanonicalString());
        Assert.Equal("N:0.05", wb.GetValue("B1").ToCanonicalString());
        Assert.Equal("B:TRUE", wb.GetValue("C1").ToCanonicalString());
        Assert.Equal("'=A1", wb.GetInput("A2"));
        Assert.Equal("T:=A1", wb.GetValue("A2").ToCanonicalString());
        Assert.Equal("T:'x", wb.GetValue("B2").ToCanonicalString());
        Assert.Equal("BLANK", wb.GetValue("C2").ToCanonicalString());
    }

    [Fact]
    public void ToEdits_OffsetsFromTheTopLeft_RowMajor_EmptyFieldsClear()
    {
        var edits = DelimitedTextMapping.ToEdits([["a", ""], ["b"]], CellAddress.Parse("C5"));
        Assert.Equal(
            [new("C5", "a"), new("D5", ""), new("C6", "b")],
            edits.ToArray());
    }

    [Fact]
    public void ToEdits_BeyondTheGrid_IsAnArgumentError()
    {
        Assert.Throws<ArgumentException>(() => DelimitedTextMapping.ToEdits([["a", "b"]], CellAddress.Parse("ZZ1")));
        Assert.Throws<ArgumentException>(() => DelimitedTextMapping.ToEdits([["a"], ["b"]], CellAddress.Parse("A100000")));
        Assert.Equal(2, DelimitedTextMapping.ToEdits([["a", "b"]], CellAddress.Parse("ZY100000")).Count); // exactly fits
    }
}
