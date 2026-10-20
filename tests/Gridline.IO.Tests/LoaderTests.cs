using System.Text;
using Gridline.Core;

namespace Gridline.IO.Tests;

/// <summary>Strict loader (§8.2, R27).</summary>
public class LoaderTests
{
    private static Workbook Load(string json) => GridlineSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    private static string Doc(string cells = "[]", string names = "{}", string version = "1", string format = "\"gridline\"") =>
        "{\"format\":" + format + ",\"version\":" + version + ",\"names\":" + names + ",\"cells\":" + cells + "}";

    private static void Bad(string json) => Assert.Throws<GridlineFormatException>(() => Load(json));

    [Fact]
    public void LoadThenSave_CanonicalFile_IsIdentical()
    {
        var canonical = WriterTests.Lines(
            "{", "  \"format\": \"gridline\",", "  \"version\": 1,", "  \"names\": {},", "  \"cells\": [",
            "    [\"A1\", \"10\"],", "    [\"B1\", \"=A1*2\"],", "    [\"A2\", \"tab\\there \\\"q\\\" \\u0001 é \U0001F600 / \\\\\"]", "  ]", "}");
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var wb = GridlineSerializer.Deserialize(bytes);
        Assert.Equal(bytes, GridlineSerializer.Serialize(wb));
        Assert.Equal("tab\there \"q\" \u0001 é \U0001F600 / \\", wb.GetInput("A2"));
        Assert.Equal(20.0, wb.GetValue("B1").Number);
    }

    [Fact]
    public void Deserialize_FromStream()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(Doc("[[\"A1\",\"5\"]]")));
        Assert.Equal("5", GridlineSerializer.Deserialize(ms).GetInput("A1"));
    }

    [Fact]
    public void AnyWhitespace_AnyKeyOrder_UnknownTopLevelKeysIgnored()
    {
        var json = " \t\r\n{ \"extra\" : { \"a\": [1, 2, {\"x\": null}] , \"b\": true },\n\"cells\"\t:[ [ \"B2\" ,\r\n \"x\" ] ] ,\"names\":{ } ,\"version\" : 1 , \"zzz\": 1.5e3, \"format\":\"gridline\" }\n\n";
        var wb = Load(json);
        Assert.Equal("x", wb.GetInput("B2"));
        Assert.Single(wb.GetUsedAddresses());
    }

    [Fact]
    public void LeadingBom_IsTolerated()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Doc("[[\"A1\",\"1\"]]"))).ToArray();
        Assert.Equal("1", GridlineSerializer.Deserialize(bytes).GetInput("A1"));
    }

    [Fact]
    public void LoneSurrogateEscapes_RoundTrip()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "\ud800");
        wb.SetCell("A2", "x\udc00");
        wb.SetCell("A3", "😀");
        var bytes = GridlineSerializer.Serialize(wb);
        var back = GridlineSerializer.Deserialize(bytes);
        Assert.Equal("\ud800", back.GetInput("A1"));
        Assert.Equal("x\udc00", back.GetInput("A2"));
        Assert.Equal("\U0001F600", back.GetInput("A3"));
        Assert.Equal(bytes, GridlineSerializer.Serialize(back));
    }

    [Fact]
    public void UnicodeEscapes_UppercaseHexAndSurrogatePair_Accepted()
    {
        var wb = Load(Doc("[[\"A1\",\"\\u00E9\\uD83D\\uDE00\\/\"]]"));
        Assert.Equal("é\U0001F600/", wb.GetInput("A1"));
    }

    [Fact]
    public void VersionAbove1_MessageContainsUnsupportedVersion()
    {
        var ex = Assert.Throws<GridlineFormatException>(() => Load(Doc(version: "2")));
        Assert.Contains("unsupported version", ex.Message);
        ex = Assert.Throws<GridlineFormatException>(() => Load(Doc(version: "99999999999999999999")));
        Assert.Contains("unsupported version", ex.Message);
        // Unsupported version wins even when later content is malformed, regardless of key order.
        ex = Assert.Throws<GridlineFormatException>(() => Load("{\"cells\":[[\"zz\"]],\"format\":\"gridline\",\"version\":3,\"names\":{}}"));
        Assert.Contains("unsupported version", ex.Message);
    }

    // ---- R27 failure matrix: one test per case ----

    [Fact] public void Fail_InvalidJson() => Bad("{\"format\": ");
    [Fact] public void Fail_NotJsonAtAll() => Bad("hello");
    [Fact] public void Fail_Empty() => Bad("");
    [Fact] public void Fail_TrailingContent() => Bad(Doc() + " x");
    [Fact] public void Fail_SecondRootValue() => Bad(Doc() + Doc());
    [Fact] public void Fail_TrailingComma() => Bad(Doc("[[\"A1\",\"1\"],]"));
    [Fact] public void Fail_Comment() => Bad("/* c */" + Doc());
    [Fact] public void Fail_DuplicateKey_TopLevel() => Bad("{\"format\":\"gridline\",\"format\":\"gridline\",\"version\":1,\"names\":{},\"cells\":[]}");
    [Fact] public void Fail_DuplicateKey_Cells() => Bad("{\"format\":\"gridline\",\"version\":1,\"names\":{},\"cells\":[],\"cells\":[]}");
    [Fact] public void Fail_DuplicateKey_UnknownKey() => Bad("{\"x\":1,\"x\":2,\"format\":\"gridline\",\"version\":1,\"names\":{},\"cells\":[]}");
    [Fact] public void Fail_DuplicateKey_NestedInUnknown() => Bad("{\"x\":{\"a\":1,\"a\":2},\"format\":\"gridline\",\"version\":1,\"names\":{},\"cells\":[]}");
    [Fact] public void Fail_DuplicateKey_EscapedSpelling() => Bad("{\"format\":\"gridline\",\"f\\u006frmat\":\"gridline\",\"version\":1,\"names\":{},\"cells\":[]}");
    [Fact] public void Fail_RootIsArray() => Bad("[]");
    [Fact] public void Fail_RootIsString() => Bad("\"gridline\"");
    [Fact] public void Fail_FormatMissing() => Bad("{\"version\":1,\"names\":{},\"cells\":[]}");
    [Fact] public void Fail_FormatWrongValue() => Bad(Doc(format: "\"spreadsheet\""));
    [Fact] public void Fail_FormatWrongCase() => Bad(Doc(format: "\"Gridline\""));
    [Fact] public void Fail_FormatNotString() => Bad(Doc(format: "1"));
    [Fact] public void Fail_VersionMissing() => Bad("{\"format\":\"gridline\",\"names\":{},\"cells\":[]}");
    [Fact] public void Fail_VersionString() => Bad(Doc(version: "\"1\""));
    [Fact] public void Fail_VersionFractional() => Bad(Doc(version: "1.5"));
    [Fact] public void Fail_VersionFractionalExponent() => Bad(Doc(version: "15e-1"));
    [Fact] public void Fail_VersionIntegralAboveOne() => Bad(Doc(version: "2.0"));
    [Fact] public void Fail_VersionHugeExponent() => Bad(Doc(version: "1e400"));

    // R54 (§8.2): any integral JSON number is the integer it denotes.
    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("1E+0")]
    [InlineData("10e-1")]
    [InlineData("0.1e1")]
    public void IntegralVersionForms_AreVersion1(string version) =>
        Assert.Equal("5", Load(Doc("[[\"A1\",\"5\"]]", version: version)).GetInput("A1"));

    [Fact]
    public void VersionIntegralAboveOne_IsUnsupported()
    {
        var ex = Assert.Throws<GridlineFormatException>(() => Load(Doc(version: "2.0")));
        Assert.Contains("unsupported version", ex.Message);
        ex = Assert.Throws<GridlineFormatException>(() => Load(Doc(version: "1e400")));
        Assert.Contains("unsupported version", ex.Message);
    }
    [Fact] public void Fail_VersionZero() => Bad(Doc(version: "0"));
    [Fact] public void Fail_VersionNegative() => Bad(Doc(version: "-1"));
    [Fact] public void Fail_VersionNull() => Bad(Doc(version: "null"));
    // R54 (§8.2): a missing "names" or "cells" key counts as empty.
    [Fact]
    public void NamesMissing_CountsAsEmpty() =>
        Assert.Equal("1", Load("{\"format\":\"gridline\",\"version\":1,\"cells\":[[\"A1\",\"1\"]]}").GetInput("A1"));

    [Fact]
    public void CellsMissing_CountsAsEmpty() =>
        Assert.Empty(Load("{\"format\":\"gridline\",\"version\":1,\"names\":{}}").GetUsedAddresses());

    [Fact]
    public void NamesAndCellsMissing_CountAsEmpty() =>
        Assert.Empty(Load("{\"format\":\"gridline\",\"version\":1}").GetUsedAddresses());
    [Fact] public void Fail_NamesNotObject() => Bad(Doc(names: "[]"));
    [Fact] public void Fail_NamesNonEmpty_Phase1() => Bad(Doc(names: "{\"X\":\"A1\"}"));
    [Fact] public void Fail_CellsNotArray() => Bad(Doc(cells: "{}"));
    [Fact] public void Fail_CellsNull() => Bad(Doc(cells: "null"));
    [Fact] public void Fail_EntryNotArray() => Bad(Doc("[\"A1\"]"));
    [Fact] public void Fail_EntryObject() => Bad(Doc("[{\"A1\":\"1\"}]"));
    [Fact] public void Fail_EntryEmptyArray() => Bad(Doc("[[]]"));
    [Fact] public void Fail_EntryOneElement() => Bad(Doc("[[\"A1\"]]"));
    [Fact] public void Fail_EntryThreeElements() => Bad(Doc("[[\"A1\",\"1\",\"x\"]]"));
    [Fact] public void Fail_EntryAddressNotString() => Bad(Doc("[[1,\"1\"]]"));
    [Fact] public void Fail_EntryInputNotString_Number() => Bad(Doc("[[\"A1\",1]]"));
    [Fact] public void Fail_EntryInputNotString_Null() => Bad(Doc("[[\"A1\",null]]"));
    [Fact] public void Fail_EntryInputNotString_Bool() => Bad(Doc("[[\"A1\",true]]"));
    // §8.2 + §4.2: any valid address loads (case-insensitive, optional `$` markers) and is stored canonically.
    [Theory]
    [InlineData("a1", "A1")]
    [InlineData("zz100000", "ZZ100000")]
    [InlineData("$A$1", "A1")]
    [InlineData("A$1", "A1")]
    [InlineData("$b7", "B7")]
    public void NonCanonicalValidAddress_Loads(string address, string canonical)
    {
        var wb = Load(Doc("[[\"" + address + "\",\"5\"]]"));
        Assert.Equal([canonical], wb.GetUsedAddresses());
        Assert.Equal("5", wb.GetInput(canonical));
    }

    // §8.2: duplicates are compared case-insensitively (and ignoring `$`), so these fail.
    [Fact] public void Fail_AddressDuplicate_CaseInsensitive() => Bad(Doc("[[\"A1\",\"1\"],[\"a1\",\"2\"]]"));
    [Fact] public void Fail_AddressDuplicate_DollarMarkers() => Bad(Doc("[[\"b2\",\"1\"],[\"$B$2\",\"2\"]]"));
    [Fact] public void Fail_AddressLoneDollar() => Bad(Doc("[[\"$\",\"1\"]]"));
    [Fact] public void Fail_AddressEmpty() => Bad(Doc("[[\"\",\"1\"]]"));
    [Fact] public void Fail_AddressGarbage() => Bad(Doc("[[\"hello\",\"1\"]]"));
    [Fact] public void Fail_AddressWhitespace() => Bad(Doc("[[\" A1\",\"1\"]]"));
    [Fact] public void Fail_AddressLeadingZero() => Bad(Doc("[[\"A01\",\"1\"]]"));
    [Fact] public void Fail_AddressRange() => Bad(Doc("[[\"A1:B2\",\"1\"]]"));
    [Fact] public void Fail_AddressRowZero() => Bad(Doc("[[\"A0\",\"1\"]]"));
    [Fact] public void Fail_AddressRowTooLarge() => Bad(Doc("[[\"A100001\",\"1\"]]"));
    [Fact] public void Fail_AddressColumnTooLarge() => Bad(Doc("[[\"XFD1\",\"1\"]]"));
    [Fact] public void Fail_AddressColumnJustPastZZ() => Bad(Doc("[[\"AAA1\",\"1\"]]"));
    [Fact] public void Fail_AddressDuplicate() => Bad(Doc("[[\"A1\",\"1\"],[\"B1\",\"2\"],[\"A1\",\"3\"]]"));
    // R54 (§8.2): an empty input is a blank cell and is skipped; a duplicate address still fails.
    [Fact]
    public void EmptyInput_IsBlankAndSkipped()
    {
        var wb = Load(Doc("[[\"A1\",\"\"],[\"B1\",\"=A1+1\"]]"));
        Assert.Equal(["B1"], wb.GetUsedAddresses());
        Assert.Equal("", wb.GetInput("A1"));
        Assert.Equal("N:1", wb.GetValue("B1").ToCanonicalString());
    }

    [Fact] public void Fail_InputEmpty_DuplicateAddress() => Bad(Doc("[[\"A1\",\"\"],[\"A1\",\"x\"]]"));
    [Fact] public void Fail_InputEmpty_DuplicateAddressBothEmpty() => Bad(Doc("[[\"a1\",\"\"],[\"A1\",\"\"]]"));
    [Fact] public void Fail_InputTooLong() => Bad(Doc("[[\"A1\",\"" + new string('x', Workbook.MaxInputLength + 1) + "\"]]"));

    [Fact]
    public void Boundary_MaxLengthInputAndLastCell_Accepted()
    {
        var wb = Load(Doc("[[\"A1\",\"" + new string('x', Workbook.MaxInputLength) + "\"],[\"ZZ100000\",\"1\"]]"));
        Assert.Equal(Workbook.MaxInputLength, wb.GetInput("A1").Length);
        Assert.Equal("1", wb.GetInput("ZZ100000"));
    }

    [Fact] public void Fail_InvalidUtf8_InString() => AssertBadBytes(Prefix("[[\"A1\",\"", new byte[] { 0xFF, 0xFE }, "\"]]"));
    [Fact] public void Fail_InvalidUtf8_Truncated4ByteSequence() => AssertBadBytes(Prefix("[[\"A1\",\"", new byte[] { 0xF0, 0x9F, 0x98 }, "\"]]"));
    [Fact] public void Fail_InvalidUtf8_OverlongEncoding() => AssertBadBytes(Prefix("[[\"A1\",\"", new byte[] { 0xC0, 0xAF }, "\"]]"));
    [Fact] public void Fail_InvalidUtf8_EncodedSurrogate() => AssertBadBytes(Prefix("[[\"A1\",\"", new byte[] { 0xED, 0xA0, 0x80 }, "\"]]"));
    [Fact] public void Fail_InvalidUtf8_InUnknownKeyValue() =>
        AssertBadBytes(Encoding.UTF8.GetBytes("{\"x\":\"").Concat(new byte[] { 0xFF }).Concat(Encoding.UTF8.GetBytes("\",\"format\":\"gridline\",\"version\":1,\"names\":{},\"cells\":[]}")).ToArray());

    private static byte[] Prefix(string head, byte[] bad, string tail) =>
        Encoding.UTF8.GetBytes("{\"format\":\"gridline\",\"version\":1,\"names\":{},\"cells\":" + head)
            .Concat(bad).Concat(Encoding.UTF8.GetBytes(tail + "}")).ToArray();

    private static void AssertBadBytes(byte[] bytes) =>
        Assert.Throws<GridlineFormatException>(() => GridlineSerializer.Deserialize(bytes));

    [Fact]
    public void Fail_Stream_InvalidUtf8()
    {
        using var ms = new MemoryStream(Prefix("[[\"A1\",\"", new byte[] { 0xFF }, "\"]]"));
        Assert.Throws<GridlineFormatException>(() => GridlineSerializer.Deserialize(ms));
    }

    [Fact]
    public void Fail_DeepNestingInUnknownKey_IsFormatException_NotStackOverflow()
    {
        var deep = new string('[', 100000) + new string(']', 100000);
        Bad("{\"x\":" + deep + ",\"format\":\"gridline\",\"version\":1,\"names\":{},\"cells\":[]}");
    }

    [Fact]
    public void SyntacticallyBadFormula_LoadsAsStoredInput_NotFormatError()
    {
        // A syntactically bad formula is valid file content (§4.3: stored, evaluates to #PARSE!).
        var wb = Load(Doc("[[\"A1\",\"=1+\"]]"));
        Assert.Equal("=1+", wb.GetInput("A1"));
    }

    [Fact]
    public void FailedLoad_ReturnsNothing_AndLeavesNoSharedState()
    {
        // Every failure throws before a Workbook exists; a following good load is unaffected.
        Bad(Doc("[[\"A1\",\"1\"],[\"A1\",\"2\"]]"));
        var wb = Load(Doc("[[\"A1\",\"1\"]]"));
        Assert.Equal("1", wb.GetInput("A1"));
    }

    [Fact]
    public void Load_RunsFullRecalc_ValuesAndEvaluatedCountAreCorrect()
    {
        var wb = Load(Doc("[[\"A1\",\"2\"],[\"A2\",\"=A1*3\"],[\"A3\",\"=A2+A1\"]]"));
        Assert.Equal(8.0, wb.GetValue("A3").Number);
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount);
    }
}
