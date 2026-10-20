namespace Gridline.Core.Tests;

// Requirement IDs: §9.1 (value types, error codes), §8.3 (canonical value strings).
public class CellValueTests
{
    [Fact]
    public void ErrorCodes_AreExact()
    {
        Assert.Equal("#DIV/0!", ErrorCodes.Div0);
        Assert.Equal("#N/A", ErrorCodes.NA);
        Assert.Equal("#NAME?", ErrorCodes.Name);
        Assert.Equal("#NUM!", ErrorCodes.Num);
        Assert.Equal("#REF!", ErrorCodes.Ref);
        Assert.Equal("#VALUE!", ErrorCodes.Value);
        Assert.Equal("#CIRC!", ErrorCodes.Circ);
        Assert.Equal("#PARSE!", ErrorCodes.Parse);
    }

    [Theory]
    [InlineData("#div/0!", "#DIV/0!")]
    [InlineData("#N/a", "#N/A")]
    [InlineData("#Parse!", "#PARSE!")]
    public void ErrorCodes_TryGetCanonical_NormalisesCase(string input, string expected)
    {
        Assert.True(ErrorCodes.TryGetCanonical(input, out var canonical));
        Assert.Equal(expected, canonical);
        Assert.False(ErrorCodes.TryGetCanonical("#null!", out _));
        Assert.False(ErrorCodes.TryGetCanonical(null, out _));
    }

    [Fact]
    public void ErrorCodes_IsKnown()
    {
        Assert.True(ErrorCodes.IsKnown("#N/A"));
        Assert.False(ErrorCodes.IsKnown("bad"));
        Assert.False(ErrorCodes.IsKnown("#null!"));
        Assert.False(ErrorCodes.IsKnown(null!));
    }

    // §8.3: EscapeText escapes exactly backslash, tab, LF, CR.
    [Theory]
    [InlineData("", "")]
    [InlineData("plain é €", "plain é €")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("a\tb", "a\\tb")]
    [InlineData("a\nb", "a\\nb")]
    [InlineData("a\rb", "a\\rb")]
    [InlineData("\\\t\n\r", "\\\\\\t\\n\\r")]
    [InlineData("\\t", "\\\\t")]
    public void EscapeText_EscapesBackslashTabLfCr(string input, string expected)
    {
        Assert.Equal(expected, CellValue.EscapeText(input));
        Assert.Equal("T:" + expected, CellValue.FromText(input).ToCanonicalString());
    }

    [Fact]
    public void Blank_IsBlank()
    {
        Assert.Equal(ValueKind.Blank, CellValue.Blank.Kind);
        Assert.Equal(ValueKind.Blank, default(CellValue).Kind);
        Assert.Equal("BLANK", CellValue.Blank.ToCanonicalString());
    }

    [Theory]
    [InlineData(1.0, "N:1")]
    [InlineData(0.1, "N:0.1")]
    [InlineData(-2.5, "N:-2.5")]
    [InlineData(1e21, "N:1E+21")]
    public void Number_Canonical(double d, string expected)
    {
        var v = CellValue.FromNumber(d);
        Assert.Equal(ValueKind.Number, v.Kind);
        Assert.Equal(d, v.Number);
        Assert.Equal(expected, v.ToCanonicalString());
    }

    [Fact]
    public void NegativeZero_PrintsAsZero()
    {
        Assert.Equal("N:0", CellValue.FromNumber(-0.0).ToCanonicalString());
        Assert.Equal(CellValue.FromNumber(0.0), CellValue.FromNumber(-0.0));
        Assert.Equal(CellValue.FromNumber(0.0).GetHashCode(), CellValue.FromNumber(-0.0).GetHashCode());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFinite_BecomesNumError(double d)
    {
        var v = CellValue.FromNumber(d);
        Assert.Equal(ValueKind.Error, v.Kind);
        Assert.Equal(ErrorCodes.Num, v.Error);
        Assert.Equal("E:#NUM!", v.ToCanonicalString());
    }

    [Fact]
    public void Text_Canonical_EscapesOnlyFour()
    {
        Assert.Equal("T:hello", CellValue.FromText("hello").ToCanonicalString());
        Assert.Equal("T:", CellValue.FromText("").ToCanonicalString());
        const string B = "\\"; // one backslash character
        Assert.Equal("T:a" + B + B + "b" + B + "tc" + B + "nd" + B + "re", CellValue.FromText("a" + B + "b" + (char)9 + "c" + (char)10 + "d" + (char)13 + "e").ToCanonicalString());
        Assert.Equal("T:\"q\" é/", CellValue.FromText("\"q\" é/").ToCanonicalString());
    }

    [Fact]
    public void FromText_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => CellValue.FromText(null!));

    [Fact]
    public void Boolean_Canonical()
    {
        Assert.Equal("B:TRUE", CellValue.FromBoolean(true).ToCanonicalString());
        Assert.Equal("B:FALSE", CellValue.FromBoolean(false).ToCanonicalString());
        Assert.True(CellValue.FromBoolean(true).Boolean);
    }

    [Fact]
    public void Error_Canonical()
    {
        var v = CellValue.FromError(ErrorCodes.Div0);
        Assert.Equal(ValueKind.Error, v.Kind);
        Assert.Equal("#DIV/0!", v.Error);
        Assert.Equal("E:#DIV/0!", v.ToCanonicalString());
    }

    [Fact]
    public void FromError_Unknown_Throws()
    {
        Assert.Throws<ArgumentException>(() => CellValue.FromError("bad"));
        Assert.Throws<ArgumentException>(() => CellValue.FromError(null!));
    }

    // §9.1: each payload accessor returns its default unless Kind matches; it never throws.
    public static TheoryData<CellValue> EveryKind => new()
    {
        CellValue.Blank,
        default(CellValue),
        CellValue.FromNumber(7.5),
        CellValue.FromText("hi"),
        CellValue.FromBoolean(true),
        CellValue.FromError(ErrorCodes.Div0),
    };

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void Number_Is0UnlessNumber(CellValue v)
    {
        Assert.Equal(v.Kind == ValueKind.Number ? 7.5 : 0, v.Number);
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void Text_IsEmptyUnlessText(CellValue v)
    {
        Assert.Equal(v.Kind == ValueKind.Text ? "hi" : "", v.Text);
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void Boolean_IsFalseUnlessBoolean(CellValue v)
    {
        Assert.Equal(v.Kind == ValueKind.Boolean, v.Boolean);
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void Error_IsEmptyUnlessError(CellValue v)
    {
        Assert.Equal(v.Kind == ValueKind.Error ? "#DIV/0!" : "", v.Error);
    }

    [Fact]
    public void Accessors_DefaultsForFalsyPayloads()
    {
        // A FALSE boolean, a zero number and empty text are distinguishable from the defaults only through Kind.
        Assert.False(CellValue.FromBoolean(false).Boolean);
        Assert.Equal(0, CellValue.FromNumber(-0.0).Number);
        Assert.Equal("", CellValue.FromText("").Text);
        Assert.Equal("", CellValue.FromText("").Error);
        Assert.Equal(0, CellValue.FromError(ErrorCodes.Num).Number);
    }

    [Fact]
    public void Equality()
    {
        Assert.Equal(CellValue.FromText("a"), CellValue.FromText("a"));
        Assert.NotEqual(CellValue.FromText("a"), CellValue.FromText("A"));
        Assert.Equal(CellValue.FromText("a").GetHashCode(), CellValue.FromText("a").GetHashCode());
        Assert.NotEqual(CellValue.FromNumber(0), CellValue.Blank);
        Assert.NotEqual(CellValue.FromText(""), CellValue.Blank);
        Assert.NotEqual(CellValue.FromBoolean(false), CellValue.FromNumber(0));
        Assert.True(CellValue.FromNumber(2) == CellValue.FromNumber(2));
        Assert.True(CellValue.FromNumber(2) != CellValue.FromNumber(3));
        Assert.False(CellValue.FromNumber(2).Equals((object?)null));
        Assert.Equal(CellValue.FromError("#REF!"), CellValue.FromError("#REF!"));
        Assert.NotEqual(CellValue.FromError("#REF!"), CellValue.FromError("#N/A"));
    }
}
