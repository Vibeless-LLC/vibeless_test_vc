using System.Text;
using Gridline.Core;

namespace Gridline.IO.Tests;

/// <summary>The .gridline format version 2 (CR1.5, R86, §8.1, §8.2, §8.4): the byte-exact multi-sheet layout, version 1
/// migration, and the loader's rejections.</summary>
public class FormatVersion2Tests
{
    private static Workbook Load(string json) => GridlineSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    /// <summary>A compact version 2 document. <paramref name="sheets"/> is the JSON of the sheets array.</summary>
    private static string V2(string sheets = "[{\"name\":\"Sheet1\",\"cells\":[]}]", string names = "{}",
        string active = "\"Sheet1\"", string version = "2") =>
        "{\"format\":\"gridline\",\"version\":" + version + ",\"activeSheet\":" + active + ",\"names\":" + names +
        ",\"sheets\":" + sheets + "}";

    private static GridlineFormatException Bad(string json) => Assert.Throws<GridlineFormatException>(() => Load(json));

    // The literal CR1.5 example.
    private static readonly string Cr15Example = WriterTests.Lines(
        "{",
        "  \"format\": \"gridline\",",
        "  \"version\": 2,",
        "  \"activeSheet\": \"Sheet1\",",
        "  \"names\": {",
        "    \"Rate\": \"Data!$B$1\"",
        "  },",
        "  \"sheets\": [",
        "    {",
        "      \"name\": \"Sheet1\",",
        "      \"cells\": [",
        "        [\"A1\", \"=Data!B2*Rate\"]",
        "      ]",
        "    },",
        "    {",
        "      \"name\": \"Data\",",
        "      \"cells\": []",
        "    }",
        "  ]",
        "}");

    [Fact]
    public void Cr15Example_LoadsEvaluatesAndResavesByteForByte()
    {
        var bytes = Encoding.UTF8.GetBytes(Cr15Example);
        var wb = GridlineSerializer.Deserialize(bytes);
        Assert.Equal(new[] { "Sheet1", "Data" }, wb.SheetNames);
        Assert.Equal("Sheet1", wb.ActiveSheet);
        Assert.Equal(new KeyValuePair<string, string>("Rate", "Data!$B$1"), Assert.Single(wb.GetNames()));
        Assert.Equal("=Data!B2*Rate", wb.GetInput("A1"));
        Assert.Equal("N:0", wb.GetValue("A1").ToCanonicalString()); // both operands blank
        Assert.Equal(1, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.False(wb.IsDirty);
        Assert.False(wb.CanUndo);
        Assert.Equal(bytes, GridlineSerializer.Serialize(wb));

        // The formula is live across sheets: Data!B2 and the name Rate (Data!$B$1) both feed it.
        wb.SetCell("Data!B2", "3");
        wb.SetCell("Data!B1", "4");
        Assert.Equal(12.0, wb.GetValue("Sheet1!A1").Number);
    }

    [Fact]
    public void NewWorkbook_SavesAsVersion2_WithActiveSheet1()
    {
        var text = WriterTests.Text(GridlineSerializer.Serialize(new Workbook()));
        Assert.Equal(WriterTests.OneSheet([]), text);
        Assert.StartsWith("{\n  \"format\": \"gridline\",\n  \"version\": 2,\n  \"activeSheet\": \"Sheet1\",\n", text);
    }

    /// <summary>Regression (the interim D-53 bridge wrote unloadable files): names into a non-first sheet, a quoted sheet
    /// name and a non-first active sheet save, load to an equal state, and re-save byte-identically.</summary>
    [Fact]
    public void MultiSheet_NamesIntoOtherSheets_QuotedName_NonFirstActive_RoundTripsByteExact()
    {
        var wb = new Workbook();
        wb.AddSheet("Data");
        wb.AddSheet("My Sheet");
        wb.SetCell("A1", "=Data!B2*Rate");
        wb.SetCell("Data!B2", "3");
        wb.SetCell("Data!B1", "4");
        wb.SetCell("'My Sheet'!C3", "=SUM(Data!B1:B2)+Far");
        wb.DefineName("Rate", "Data!$B$1");
        wb.DefineName("Far", "'my sheet'!$c$1");
        wb.ActiveSheet = "My Sheet";

        var expected = WriterTests.Lines(
            "{",
            "  \"format\": \"gridline\",",
            "  \"version\": 2,",
            "  \"activeSheet\": \"My Sheet\",",
            "  \"names\": {",
            "    \"Far\": \"'My Sheet'!$C$1\",",
            "    \"Rate\": \"Data!$B$1\"",
            "  },",
            "  \"sheets\": [",
            "    {",
            "      \"name\": \"Sheet1\",",
            "      \"cells\": [",
            "        [\"A1\", \"=Data!B2*Rate\"]",
            "      ]",
            "    },",
            "    {",
            "      \"name\": \"Data\",",
            "      \"cells\": [",
            "        [\"B1\", \"4\"],",
            "        [\"B2\", \"3\"]",
            "      ]",
            "    },",
            "    {",
            "      \"name\": \"My Sheet\",",
            "      \"cells\": [",
            "        [\"C3\", \"=SUM(Data!B1:B2)+Far\"]",
            "      ]",
            "    }",
            "  ]",
            "}");
        var bytes = GridlineSerializer.Serialize(wb);
        Assert.Equal(expected, WriterTests.Text(bytes));

        var back = GridlineSerializer.Deserialize(bytes);
        Assert.True(RoundTripPropertyTests.SameState(wb, back));
        Assert.Equal("My Sheet", back.ActiveSheet);
        Assert.Equal(12.0, back.GetValue("Sheet1!A1").Number);
        Assert.Equal(7.0, back.GetValue("'My Sheet'!C3").Number);
        Assert.Equal(2, back.LastRecalc.EvaluatedFormulaCount);
        Assert.False(back.IsDirty);
        Assert.False(back.CanUndo);
        Assert.Equal(bytes, GridlineSerializer.Serialize(back));

        // Unqualified addresses on the loaded workbook mean the loaded active sheet.
        back.SetCell("C1", "10");
        Assert.Equal(17.0, back.GetValue("C3").Number);
    }

    [Fact]
    public void Sheets_AreWrittenInWorkbookOrder_NotSorted_WithEscapedNames()
    {
        var wb = new Workbook();
        wb.AddSheet("zeta");
        wb.AddSheet("q\"t");
        wb.AddSheet("日本", 0);
        wb.MoveSheet("Sheet1", 3);
        wb.SetCell("'q\"t'!A1", "x");
        wb.ActiveSheet = "q\"t";
        var text = WriterTests.Text(GridlineSerializer.Serialize(wb));
        Assert.Contains("  \"activeSheet\": \"q\\\"t\",\n", text);
        int a = text.IndexOf("\"name\": \"日本\"", StringComparison.Ordinal);
        int b = text.IndexOf("\"name\": \"zeta\"", StringComparison.Ordinal);
        int c = text.IndexOf("\"name\": \"q\\\"t\"", StringComparison.Ordinal);
        int d = text.IndexOf("\"name\": \"Sheet1\"", StringComparison.Ordinal);
        Assert.True(a > 0 && a < b && b < c && c < d, text);
        var back = GridlineSerializer.Deserialize(Encoding.UTF8.GetBytes(text));
        Assert.Equal(wb.SheetNames, back.SheetNames);
        Assert.Equal("x", back.GetInput("A1"));
    }

    [Fact]
    public void SameAddressOnDifferentSheets_IsNotADuplicate()
    {
        var wb = Load(V2("[{\"name\":\"A\",\"cells\":[[\"A1\",\"1\"]]},{\"name\":\"B\",\"cells\":[[\"a1\",\"=A!A1+1\"]]}]", active: "\"A\""));
        Assert.Equal(2.0, wb.GetValue("B!A1").Number);
    }

    // ---- Version 1 migration (CR1.5) ----

    [Fact]
    public void Version1_MigratesToSheet1_NamesQualified_FormulasUnchanged()
    {
        var wb = Load("{\"format\":\"gridline\",\"version\":1,\"names\":{\"Rate\":\"$B$1\",\"Gone\":\"#REF!\",\"Total\":\"$a$1:$a$10\"},"
            + "\"cells\":[[\"A1\",\"=SUM(B1:B3)*Rate\"],[\"B1\",\"5\"],[\"A2\",\"=Gone\"]]}");
        Assert.Equal(new[] { "Sheet1" }, wb.SheetNames);
        Assert.Equal("Sheet1", wb.ActiveSheet);
        Assert.Equal(
            new KeyValuePair<string, string>[] { new("Gone", "#REF!"), new("Rate", "Sheet1!$B$1"), new("Total", "Sheet1!$A$1:$A$10") },
            wb.GetNames());
        Assert.Equal("=SUM(B1:B3)*Rate", wb.GetInput("A1"));
        Assert.Equal(25.0, wb.GetValue("A1").Number);
        Assert.Equal("#REF!", wb.GetValue("A2").Error);
        Assert.False(wb.IsDirty);
        Assert.False(wb.CanUndo);
    }

    /// <summary>Controller ruling: a version 1 file keeps the strict version 1 name syntax, so a qualified value is a
    /// format error, even one naming Sheet1.</summary>
    [Theory]
    [InlineData("Sheet1!$B$1")]
    [InlineData("sheet1!$B$1")]
    [InlineData("'Sheet1'!$A$1:$A$2")]
    [InlineData("Data!$B$1")]
    [InlineData("My Sheet!$B$1")]
    public void Version1_QualifiedName_IsRejected(string refersTo)
    {
        var ex = Bad("{\"format\":\"gridline\",\"version\":1,\"names\":{\"Rate\":\"" + refersTo + "\"},\"cells\":[[\"A1\",\"1\"]]}");
        Assert.Contains("Invalid .gridline file", ex.Message);
    }

    [Fact] public void Version1_WithActiveSheet_IsRejected() =>
        Bad("{\"format\":\"gridline\",\"version\":1,\"activeSheet\":\"Sheet1\",\"names\":{},\"cells\":[]}");

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"not\":\"an array\"}")]
    [InlineData("null")]
    public void Version2_WithTopLevelCells_IsRejected(string cells) =>
        Bad("{\"format\":\"gridline\",\"version\":2,\"activeSheet\":\"Sheet1\",\"names\":{},\"cells\":" + cells
            + ",\"sheets\":[{\"name\":\"Sheet1\",\"cells\":[]}]}");

    [Fact] public void Version1_WithSheets_IsRejected() =>
        Bad("{\"format\":\"gridline\",\"version\":1,\"names\":{},\"sheets\":[{\"name\":\"Sheet1\",\"cells\":[]}]}");

    // ---- Version handling ----

    [Theory]
    [InlineData("3")]
    [InlineData("3.0")]
    [InlineData("30e-1")]
    [InlineData("99999999999999999999")]
    [InlineData("1e400")]
    public void VersionAbove2_IsUnsupported(string version)
    {
        var ex = Bad(V2(version: version));
        Assert.Contains("unsupported version", ex.Message);
    }

    [Fact]
    public void UnsupportedVersion_ReportsBeforeOtherProblems()
    {
        var ex = Bad("{\"sheets\":[{\"nam\":1}],\"activeSheet\":7,\"format\":\"gridline\",\"version\":3,\"names\":{\"1x\":5}}");
        Assert.Contains("unsupported version", ex.Message);
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("2e0")]
    [InlineData("20e-1")]
    public void IntegralVersionForms_AreVersion2(string version) =>
        Assert.Equal(new[] { "Sheet1" }, Load(V2(version: version)).SheetNames);

    [Theory]
    [InlineData("2.5")]
    [InlineData("\"2\"")]
    [InlineData("0")]
    [InlineData("-2")]
    public void InvalidVersion_IsRejected(string version) => Bad(V2(version: version));

    // ---- Accepted variations (§8.2: any whitespace, key order, unknown keys; R54, R86) ----

    [Fact]
    public void AnyKeyOrder_AndUnknownKeys_AreIgnored()
    {
        var wb = Load("{\"sheets\":[{\"x\":{\"y\":[1]},\"cells\":[[\"B2\",\"7\"]],\"name\":\"Data\"}],\"extra\":null,"
            + "\"names\":{\"R\":\"data!$b$2\"},\"activeSheet\":\"DATA\",\"version\":2,\"format\":\"gridline\"}");
        Assert.Equal(new[] { "Data" }, wb.SheetNames);
        Assert.Equal("Data", wb.ActiveSheet); // matched case-insensitively, stored in the defined casing (R86, R80)
        Assert.Equal("Data!$B$2", Assert.Single(wb.GetNames()).Value);
        Assert.Equal("7", wb.GetInput("B2"));
    }

    [Fact]
    public void Version2_MissingNames_CountsAsEmpty() =>
        Assert.Empty(Load("{\"format\":\"gridline\",\"version\":2,\"activeSheet\":\"Sheet1\",\"sheets\":[{\"name\":\"Sheet1\",\"cells\":[]}]}").GetNames());

    [Fact]
    public void Version2_SheetWithoutCells_IsEmpty() =>
        Assert.Empty(Load(V2("[{\"name\":\"Sheet1\"}]")).GetUsedAddresses());

    [Theory]
    [InlineData("#REF!", "#REF!")]
    [InlineData("Data!$B$1", "Data!$B$1")]
    [InlineData("data!$b$1", "Data!$B$1")]
    [InlineData("'Data'!$B$1", "Data!$B$1")]
    [InlineData("'My Sheet'!$C$10:$A$1", "'My Sheet'!$A$1:$C$10")]
    public void Version2_QualifiedNames_LoadCanonical(string refersTo, string stored)
    {
        var wb = Load(V2("[{\"name\":\"Data\",\"cells\":[]},{\"name\":\"My Sheet\",\"cells\":[]}]", "{\"N\":\"" + refersTo + "\"}", "\"Data\""));
        Assert.Equal(stored, Assert.Single(wb.GetNames()).Value);
    }

    // ---- Rejections (CR1.5, R86): each is a GridlineFormatException ----

    public static TheoryData<string, string> Rejections => new()
    {
        // CR1.5 validation
        { "zero sheets", V2("[]") },
        { "duplicate sheet name", V2("[{\"name\":\"Data\",\"cells\":[]},{\"name\":\"Data\",\"cells\":[]}]", active: "\"Data\"") },
        { "duplicate sheet name, case-insensitive", V2("[{\"name\":\"Data\",\"cells\":[]},{\"name\":\"dATA\",\"cells\":[]}]", active: "\"Data\"") },
        { "invalid sheet name: empty", V2("[{\"name\":\"\",\"cells\":[]}]", active: "\"\"") },
        { "invalid sheet name: colon", V2("[{\"name\":\"a:b\",\"cells\":[]}]", active: "\"a:b\"") },
        { "invalid sheet name: brackets", V2("[{\"name\":\"[x]\",\"cells\":[]}]", active: "\"[x]\"") },
        { "invalid sheet name: leading quote", V2("[{\"name\":\"'x\",\"cells\":[]}]", active: "\"'x\"") },
        { "invalid sheet name: 32 characters", V2("[{\"name\":\"" + new string('s', 32) + "\",\"cells\":[]}]", active: "\"" + new string('s', 32) + "\"") },
        { "unknown activeSheet", V2(active: "\"Data\"") },
        // R86: required keys and shapes
        { "activeSheet missing", "{\"format\":\"gridline\",\"version\":2,\"names\":{},\"sheets\":[{\"name\":\"Sheet1\",\"cells\":[]}]}" },
        { "activeSheet not a string", V2(active: "1") },
        { "activeSheet null", V2(active: "null") },
        { "sheets missing", "{\"format\":\"gridline\",\"version\":2,\"activeSheet\":\"Sheet1\",\"names\":{}}" },
        { "sheets missing, version 1 cells present", "{\"format\":\"gridline\",\"version\":2,\"activeSheet\":\"Sheet1\",\"names\":{},\"cells\":[]}" },
        { "sheets not an array", V2("{\"name\":\"Sheet1\"}") },
        { "sheets null", V2("null") },
        { "sheet not an object", V2("[[\"Sheet1\"]]") },
        { "sheet name missing", V2("[{\"cells\":[]}]") },
        { "sheet name not a string", V2("[{\"name\":1,\"cells\":[]}]") },
        { "sheet cells not an array", V2("[{\"name\":\"Sheet1\",\"cells\":{}}]") },
        { "duplicate key in a sheet", V2("[{\"name\":\"Sheet1\",\"name\":\"Sheet1\",\"cells\":[]}]") },
        { "format missing", "{\"version\":2,\"activeSheet\":\"Sheet1\",\"names\":{},\"sheets\":[{\"name\":\"Sheet1\",\"cells\":[]}]}" },
        { "version missing", "{\"format\":\"gridline\",\"activeSheet\":\"Sheet1\",\"names\":{},\"sheets\":[{\"name\":\"Sheet1\",\"cells\":[]}]}" },
        // R86: names
        { "unqualified name", V2(names: "{\"Rate\":\"$B$1\"}") },
        { "unqualified range name", V2(names: "{\"Rate\":\"$A$1:$B$2\"}") },
        { "name to an unknown sheet", V2(names: "{\"Rate\":\"Data!$B$1\"}") },
        { "name not absolute", V2(names: "{\"Rate\":\"Sheet1!B1\"}") },
        { "name partly absolute", V2(names: "{\"Rate\":\"Sheet1!$B1\"}") },
        { "name with a prefix needing quotes", V2("[{\"name\":\"My Sheet\",\"cells\":[]}]", "{\"Rate\":\"My Sheet!$B$1\"}", "\"My Sheet\"") },
        { "name with a prefix on the second corner only", V2(names: "{\"Rate\":\"$A$1:Sheet1!$B$2\"}") },
        { "name #ref! in lower case", V2(names: "{\"Rate\":\"#ref!\"}") },
        { "name qualified #REF!", V2(names: "{\"Rate\":\"Sheet1!#REF!\"}") },
        { "name value empty", V2(names: "{\"Rate\":\"\"}") },
        { "name value not a string", V2(names: "{\"Rate\":1}") },
        { "invalid name", V2(names: "{\"1x\":\"Sheet1!$A$1\"}") },
        { "duplicate name, case-insensitive", V2(names: "{\"Rate\":\"Sheet1!$A$1\",\"rate\":\"Sheet1!$A$2\"}") },
        // R86: cells
        { "qualified cell address", V2("[{\"name\":\"Sheet1\",\"cells\":[[\"Sheet1!A1\",\"1\"]]}]") },
        { "qualified cell address to another sheet", V2("[{\"name\":\"Sheet1\",\"cells\":[]},{\"name\":\"Data\",\"cells\":[[\"Sheet1!A1\",\"1\"]]}]") },
        { "duplicate cell address in a sheet", V2("[{\"name\":\"Sheet1\",\"cells\":[[\"A1\",\"1\"],[\"a1\",\"2\"]]}]") },
        { "duplicate blank cell address in a sheet", V2("[{\"name\":\"Sheet1\",\"cells\":[[\"$B$2\",\"\"],[\"B2\",\"\"]]}]") },
        { "invalid cell address", V2("[{\"name\":\"Sheet1\",\"cells\":[[\"A0\",\"1\"]]}]") },
        { "cell entry with three elements", V2("[{\"name\":\"Sheet1\",\"cells\":[[\"A1\",\"1\",\"x\"]]}]") },
        { "cell input not a string", V2("[{\"name\":\"Sheet1\",\"cells\":[[\"A1\",1]]}]") },
        { "cell input too long", V2("[{\"name\":\"Sheet1\",\"cells\":[[\"A1\",\"" + new string('x', Workbook.MaxInputLength + 1) + "\"]]}]") },
        { "error in the last sheet", V2("[{\"name\":\"Sheet1\",\"cells\":[[\"A1\",\"1\"]]},{\"name\":\"Data\",\"cells\":[[\"A1\",\"1\"],[\"A1\",\"2\"]]}]") },
        // JSON
        { "trailing comma in sheets", V2("[{\"name\":\"Sheet1\",\"cells\":[]},]") },
        { "truncated", V2().Substring(0, 60) },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void Version2_Rejection(string why, string json)
    {
        var ex = Bad(json);
        Assert.StartsWith("Invalid .gridline file", ex.Message);
        Assert.NotNull(why);
    }

    [Fact]
    public void TooManySheets_IsRejected_255Accepted()
    {
        string Sheets(int n) => "[" + string.Join(",", Enumerable.Range(1, n).Select(i => "{\"name\":\"S" + i + "\",\"cells\":[]}")) + "]";
        Bad(V2(Sheets(256), active: "\"S1\""));
        Assert.Equal(255, Load(V2(Sheets(255), active: "\"S1\"")).SheetNames.Count);
    }

    [Fact]
    public void Rejection_Messages_NameTheProblem()
    {
        Assert.Contains("unqualified", Bad(V2("[{\"name\":\"Sheet1\",\"cells\":[[\"Sheet1!A1\",\"1\"]]}]")).Message);
        Assert.Contains("sheet-qualified targets", Bad(V2(names: "{\"Rate\":\"$B$1\"}")).Message);
        Assert.Contains("unqualified targets", Bad("{\"format\":\"gridline\",\"version\":1,\"names\":{\"Rate\":\"Sheet1!$B$1\"}}").Message);
        Assert.Contains("there is no sheet named 'Data'", Bad(V2(names: "{\"Rate\":\"Data!$B$1\"}")).Message);
        Assert.Contains("Unknown active sheet", Bad(V2(active: "\"Data\"")).Message);
        Assert.Contains("Duplicate sheet name", Bad(V2("[{\"name\":\"A\",\"cells\":[]},{\"name\":\"a\",\"cells\":[]}]", active: "\"A\"")).Message);
        Assert.Contains("at least one sheet", Bad(V2("[]")).Message);
    }

    // ---- No partial state (§8.2) ----

    [Fact]
    public void FailedLoad_LoadsNothing_AndAFollowingLoadIsUnaffected()
    {
        Bad(V2("[{\"name\":\"Sheet1\",\"cells\":[[\"A1\",\"1\"]]},{\"name\":\"Data\",\"cells\":[[\"A1\",\"=Sheet1!A1\"]]}]", names: "{\"R\":\"Nope!$A$1\"}"));
        var wb = Load(V2("[{\"name\":\"Sheet1\",\"cells\":[[\"A1\",\"1\"]]}]"));
        Assert.Equal(new[] { "Sheet1" }, wb.SheetNames);
        Assert.Empty(wb.GetNames());
        Assert.Equal(["A1"], wb.GetUsedAddresses());
    }

    [Fact]
    public void FailedLoadFromPath_ThrowsFormatException_AndLeavesTheFileUntouched()
    {
        string path = Path.Combine(Path.GetTempPath(), "gridline-v2-" + Guid.NewGuid().ToString("N") + ".gridline");
        try
        {
            var bad = Encoding.UTF8.GetBytes(V2(names: "{\"Rate\":\"$B$1\"}"));
            File.WriteAllBytes(path, bad);
            Assert.Throws<GridlineFormatException>(() => GridlineFile.LoadFromPath(path));
            Assert.Equal(bad, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    // ---- Atomic SaveToPath is unchanged (§8.4) and writes version 2 ----

    [Fact]
    public void SaveToPath_LoadFromPath_MultiSheet_RoundTrips()
    {
        var wb = new Workbook();
        wb.AddSheet("O'Brien");
        wb.SetCell("'O''Brien'!B2", "=Sheet1!A1*2");
        wb.SetCell("A1", "21");
        wb.DefineName("Ob", "'O''Brien'!$B$2");
        wb.ActiveSheet = "O'Brien";
        string path = Path.Combine(Path.GetTempPath(), "gridline-v2-" + Guid.NewGuid().ToString("N") + ".gridline");
        try
        {
            GridlineFile.SaveToPath(wb, path);
            Assert.Equal(GridlineSerializer.Serialize(wb), File.ReadAllBytes(path));
            var back = GridlineFile.LoadFromPath(path);
            Assert.True(RoundTripPropertyTests.SameState(wb, back));
            Assert.Equal(42.0, back.GetValue("B2").Number);
            Assert.Contains("\"Ob\": \"'O''Brien'!$B$2\"", WriterTests.Text(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }
}
