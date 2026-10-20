using System.Text;
using Gridline.Core;

namespace Gridline.IO.Tests;

/// <summary>Names in the .gridline format (§8.1, §8.2, §9.6, R58, R59).</summary>
public class NamesFileTests
{
    private static Workbook Load(string json) => GridlineSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

    private static string Doc(string names, string cells = "[]") =>
        "{\"format\":\"gridline\",\"version\":1,\"names\":" + names + ",\"cells\":" + cells + "}";

    private static void Bad(string json) => Assert.Throws<GridlineFormatException>(() => Load(json));

    // The literal §8.1 example file (architecture.md): names Rate and Total, three cells.
    [Fact]
    public void Spec81Example_Literal_LoadsEvaluatesAndResavesByteForByte()
    {
        var text = WriterTests.Lines(
            "{", "  \"format\": \"gridline\",", "  \"version\": 1,", "  \"names\": {",
            "    \"Rate\": \"$B$1\",", "    \"Total\": \"$A$1:$A$10\"", "  },", "  \"cells\": [",
            "    [\"A1\", \"=SUM(B1:B3)*Rate\"],", "    [\"B1\", \"5\"],", "    [\"A2\", \"hello \\\"world\\\"\"]", "  ]", "}");
        var bytes = Encoding.UTF8.GetBytes(text);
        var wb = GridlineSerializer.Deserialize(bytes);
        Assert.Equal(25.0, wb.GetValue("A1").Number);
        Assert.Equal("hello \"world\"", wb.GetInput("A2"));
        Assert.Equal(new[] { "Rate", "Total" }, wb.GetNames().Select(n => n.Key).ToArray());
        Assert.Equal(bytes, GridlineSerializer.Serialize(wb));
    }

    [Fact]
    public void SampleWithTwoNames_LoadsEvaluatesAndResavesByteExact()
    {
        var canonical = WriterTests.Lines(
            "{", "  \"format\": \"gridline\",", "  \"version\": 1,", "  \"names\": {",
            "    \"Rate\": \"$B$1\",", "    \"Total\": \"$A$1:$A$3\"", "  },", "  \"cells\": [",
            "    [\"A1\", \"1\"],", "    [\"B1\", \"2\"],", "    [\"C1\", \"=SUM(A1:A3)*Rate\"],", "    [\"A2\", \"2\"],", "    [\"A3\", \"3\"]", "  ]", "}");
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var wb = GridlineSerializer.Deserialize(bytes);
        Assert.Equal(12.0, wb.GetValue("C1").Number);
        Assert.Equal(bytes, GridlineSerializer.Serialize(wb));
        Assert.Equal(2, wb.GetNames().Count);
    }

    [Fact]
    public void Load_FullRecalc_NotDirtyNoHistory()
    {
        var wb = Load(Doc("{\"R\":\"$A$1\"}", "[[\"A1\",\"4\"],[\"B1\",\"=R*2\"],[\"B2\",\"=B1+1\"]]"));
        Assert.Equal(8.0, wb.GetValue("B1").Number);
        Assert.Equal(9.0, wb.GetValue("B2").Number);
        Assert.Equal(2, wb.LastRecalc.EvaluatedFormulaCount);
        Assert.False(wb.CanUndo);
        Assert.False(wb.CanRedo);
    }

    [Fact]
    public void Load_LaterEditsInsideTargetStillRecalculate()
    {
        var wb = Load(Doc("{\"R\":\"$A$1\"}", "[[\"A1\",\"4\"],[\"B1\",\"=R*2\"]]"));
        wb.SetCell("A1", "5");
        Assert.Equal(10.0, wb.GetValue("B1").Number);
    }

    [Fact]
    public void Load_RefErrorTarget_EvaluatesRef_AndResavesByteExact()
    {
        var canonical = WriterTests.Lines(
            "{", "  \"format\": \"gridline\",", "  \"version\": 1,", "  \"names\": {", "    \"Gone\": \"#REF!\"", "  },",
            "  \"cells\": [", "    [\"A1\", \"=Gone\"]", "  ]", "}");
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var wb = GridlineSerializer.Deserialize(bytes);
        Assert.Equal("#REF!", wb.GetValue("A1").Error);
        Assert.Equal(new KeyValuePair<string, string>("Gone", "#REF!"), Assert.Single(wb.GetNames()));
        Assert.Equal(bytes, GridlineSerializer.Serialize(wb));
    }

    [Fact]
    public void Load_NonCanonicalRefersTo_LoadsNormalised_ResavesCanonical()
    {
        var wb = Load(Doc("{\"a\":\"$b$1\",\"B\":\"$C$10:$A$1\"}"));
        Assert.Equal(new[] { "$B$1", "$A$1:$C$10" }, wb.GetNames().Select(n => n.Value).ToArray());
        var text = Encoding.UTF8.GetString(GridlineSerializer.Serialize(wb));
        Assert.Contains("\"a\": \"$B$1\"", text);
        Assert.Contains("\"B\": \"$A$1:$C$10\"", text);
    }

    [Fact]
    public void Load_EscapedNameAndCellText_Unaffected()
    {
        var wb = Load(Doc("{\"N\\u0078\":\"$A$1\"}", "[[\"A1\",\"say \\\"hi\\\"\\n\"]]"));
        Assert.Equal("say \"hi\"\n", wb.GetInput("A1"));
        Assert.Equal("Nx", Assert.Single(wb.GetNames()).Key);
    }

    [Theory]
    [InlineData("{\"1x\":\"$A$1\"}")]
    [InlineData("{\"\":\"$A$1\"}")]
    [InlineData("{\"A1\":\"$A$1\"}")]
    [InlineData("{\"true\":\"$A$1\"}")]
    [InlineData("{\"a b\":\"$A$1\"}")]
    [InlineData("{\"x\\n\":\"$A$1\"}")]
    [InlineData("{\"x\":\"A1\"}")]
    [InlineData("{\"x\":\"$A1\"}")]
    [InlineData("{\"x\":\"A$1\"}")]
    [InlineData("{\"x\":\"\"}")]
    [InlineData("{\"x\":\"#ref!\"}")]
    [InlineData("{\"x\":\" #REF!\"}")]
    [InlineData("{\"x\":\"$A$1:$B\"}")]
    [InlineData("{\"x\":\"$A$100001\"}")]
    [InlineData("{\"x\":\"=$A$1\"}")]
    [InlineData("{\"x\":\"$A$1\\n\"}")]
    public void Fail_InvalidNameOrRefersTo(string names) => Bad(Doc(names));

    [Fact] public void Fail_NameTooLong() => Bad(Doc("{\"" + new string('a', 256) + "\":\"$A$1\"}"));
    [Fact] public void Fail_DuplicateNameExact() => Bad(Doc("{\"X\":\"$A$1\",\"X\":\"$A$2\"}"));
    [Fact] public void Fail_DuplicateNameCaseInsensitive() => Bad(Doc("{\"Abc\":\"$A$1\",\"aBC\":\"$A$2\"}"));
    [Fact] public void Fail_ValueNumber() => Bad(Doc("{\"X\":1}"));
    [Fact] public void Fail_ValueNull() => Bad(Doc("{\"X\":null}"));
    [Fact] public void Fail_ValueObject() => Bad(Doc("{\"X\":{}}"));
    [Fact] public void Fail_ValueArray() => Bad(Doc("{\"X\":[\"$A$1\"]}"));
    [Fact] public void Fail_ValueBool() => Bad(Doc("{\"X\":true}"));

    [Fact]
    public void MaxLengthName_Accepted() =>
        Assert.Single(Load(Doc("{\"" + new string('a', 255) + "\":\"$A$1\"}")).GetNames());

    [Fact]
    public void Fail_BadName_WithValidCells_LoadsNothing()
    {
        var ex = Assert.Throws<GridlineFormatException>(() => Load(Doc("{\"ok\":\"$A$1\",\"1bad\":\"$A$1\"}", "[[\"A1\",\"1\"]]")));
        Assert.Contains("Invalid .gridline file", ex.Message);
    }

    [Fact]
    public void UnsupportedVersion_ReportsBeforeNameProblems()
    {
        var ex = Assert.Throws<GridlineFormatException>(() =>
            Load("{\"format\":\"gridline\",\"version\":2,\"names\":{\"1x\":5},\"cells\":[]}"));
        Assert.Contains("unsupported version", ex.Message);
    }

    [Fact]
    public void SerializeDeserialize_PersistsNames()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "3");
        wb.DefineName("Three", "$A$1");
        var back = GridlineSerializer.Deserialize(GridlineSerializer.Serialize(wb));
        back.SetCell("B1", "=Three+1");
        Assert.Equal(4.0, back.GetValue("B1").Number);
    }
}
