using System.Text;
using Gridline.Core;

namespace Gridline.IO.Tests;

/// <summary>Canonical writer (§8.1, R26). Expected bytes are built with explicit "\n" (no checked-in fixtures).</summary>
public class WriterTests
{
    internal static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    internal static string Text(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);

    internal static Workbook Make(params (string Address, string Input)[] cells)
    {
        var wb = new Workbook();
        foreach (var (a, i) in cells) wb.SetCell(a, i);
        return wb;
    }

    [Fact]
    public void EmptyWorkbook_ExactBytes()
    {
        var expected = Lines("{", "  \"format\": \"gridline\",", "  \"version\": 1,", "  \"names\": {},", "  \"cells\": []", "}");
        Assert.Equal(Encoding.UTF8.GetBytes(expected), GridlineSerializer.Serialize(new Workbook()));
    }

    [Fact]
    public void SampleWorkbook_ExactBytes_RowMajor()
    {
        var wb = Make(("B2", "=A1+1"), ("A1", "10"), ("C1", "hello"), ("A2", "TRUE"));
        var expected = Lines(
            "{",
            "  \"format\": \"gridline\",",
            "  \"version\": 1,",
            "  \"names\": {},",
            "  \"cells\": [",
            "    [\"A1\", \"10\"],",
            "    [\"C1\", \"hello\"],",
            "    [\"A2\", \"TRUE\"],",
            "    [\"B2\", \"=A1+1\"]",
            "  ]",
            "}");
        var bytes = GridlineSerializer.Serialize(wb);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), bytes);
        Assert.NotEqual(0xEF, bytes[0]); // no BOM
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
    }

    [Fact]
    public void RowMajor_NotColumnMajor_AndNumericRowOrder()
    {
        var wb = Make(("A10", "x"), ("B2", "y"), ("A2", "z"), ("Z1", "q"));
        var text = Text(GridlineSerializer.Serialize(wb));
        var order = new[] { "\"Z1\"", "\"A2\"", "\"B2\"", "\"A10\"" };
        int last = -1;
        foreach (var o in order)
        {
            int idx = text.IndexOf(o, StringComparison.Ordinal);
            Assert.True(idx > last, o);
            last = idx;
        }
    }

    [Fact]
    public void SerializeToStream_MatchesBytes()
    {
        var wb = Make(("A1", "1"));
        using var ms = new MemoryStream();
        GridlineSerializer.Serialize(wb, ms);
        Assert.Equal(GridlineSerializer.Serialize(wb), ms.ToArray());
    }

    [Theory]
    [InlineData("\"", "\\\"")]
    [InlineData("\\", "\\\\")]
    [InlineData("\b", "\\b")]
    [InlineData("\f", "\\f")]
    [InlineData("\n", "\\n")]
    [InlineData("\r", "\\r")]
    [InlineData("\t", "\\t")]
    [InlineData("\u0000", "\\u0000")]
    [InlineData("\u0001", "\\u0001")]
    [InlineData("\u000b", "\\u000b")]
    [InlineData("\u001f", "\\u001f")]
    [InlineData("\u001a", "\\u001a")]
    [InlineData("/", "/")]
    [InlineData("\u007f", "\u007f")]
    [InlineData("<>&'", "<>&'")]
    [InlineData("é", "é")]
    [InlineData("日本語", "日本語")]
    [InlineData("\U0001F600", "\U0001F600")]
    public void Escaping_PerClass(string input, string escaped)
    {
        var wb = Make(("A1", input));
        var expected = Lines(
            "{", "  \"format\": \"gridline\",", "  \"version\": 1,", "  \"names\": {},", "  \"cells\": [",
            "    [\"A1\", \"" + escaped + "\"]", "  ]", "}");
        Assert.Equal(expected, Text(GridlineSerializer.Serialize(wb)));
    }

    [Fact]
    public void Escaping_AllClassesInOneInput_ByteExact()
    {
        string input = "q\"b\\s/\b\f\n\r\t\u0000\u001f\u007fé日\U0001F600\ud800x\udfff<>&'";
        string escaped = "q\\\"b\\\\s/\\b\\f\\n\\r\\t\\u0000\\u001f\u007fé日\U0001F600\\ud800x\\udfff<>&'";
        var expected = Lines(
            "{", "  \"format\": \"gridline\",", "  \"version\": 1,", "  \"names\": {},", "  \"cells\": [",
            "    [\"A1\", \"" + escaped + "\"]", "  ]", "}");
        Assert.Equal(expected, Text(GridlineSerializer.Serialize(Make(("A1", input)))));
    }

    [Fact]
    public void LoneSurrogates_AreEscapedLowercase()
    {
        var text = Text(GridlineSerializer.Serialize(Make(("A1", "\ud800"), ("A2", "a\udfffb"), ("A3", "\U0001F600"))));
        Assert.Contains("[\"A1\", \"\\ud800\"]", text);
        Assert.Contains("[\"A2\", \"a\\udfffb\"]", text);
        Assert.Contains("[\"A3\", \"\U0001F600\"]", text);
    }

    [Fact]
    public void Emoji_IsWrittenAsFourByteUtf8()
    {
        var bytes = GridlineSerializer.Serialize(Make(("A1", "\U0001F600")));
        Assert.True(bytes.AsSpan().IndexOf(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }) >= 0);
    }

    [Fact]
    public void NonEmptyNames_SortedOrdinalIgnoreCase_ExactLayout()
    {
        var names = new List<KeyValuePair<string, string>>
        {
            new("zeta", "=A1"), new("Alpha", "B2"), new("beta", "say \"hi\""),
        };
        var wb = Make(("A1", "1"));
        var expected = Lines(
            "{", "  \"format\": \"gridline\",", "  \"version\": 1,", "  \"names\": {",
            "    \"Alpha\": \"B2\",", "    \"beta\": \"say \\\"hi\\\"\",", "    \"zeta\": \"=A1\"", "  },",
            "  \"cells\": [", "    [\"A1\", \"1\"]", "  ]", "}");
        Assert.Equal(expected, Text(GridlineSerializer.Serialize(wb, names)));
    }

    [Fact]
    public void NonEmptyNames_WithEmptyCells()
    {
        var names = new List<KeyValuePair<string, string>> { new("X", "A1") };
        var expected = Lines(
            "{", "  \"format\": \"gridline\",", "  \"version\": 1,", "  \"names\": {",
            "    \"X\": \"A1\"", "  },", "  \"cells\": []", "}");
        Assert.Equal(expected, Text(GridlineSerializer.Serialize(new Workbook(), names)));
    }
}
