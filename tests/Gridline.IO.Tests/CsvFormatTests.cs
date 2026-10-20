using System.Text;
using Gridline.Core;

namespace Gridline.IO.Tests;

/// <summary>CSV import and export (§8.5, R70).</summary>
public sealed class CsvFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridline-csv-" + Guid.NewGuid().ToString("N"));

    public CsvFormatTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static Workbook Imp(string text, char d = ',') => CsvFormat.Import(Encoding.UTF8.GetBytes(text), d);
    private static string Exp(Workbook wb, char d = ',') => Encoding.UTF8.GetString(CsvFormat.Export(wb, d)).TrimStart('\uFEFF');
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // ---- encoding detection ----

    [Fact]
    public void Utf8Bom_IsStripped()
    {
        var wb = CsvFormat.Import(Cat([0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes("a,é")));
        Assert.Equal("a", wb.GetInput("A1"));
        Assert.Equal("é", wb.GetInput("B1"));
    }

    [Fact]
    public void Utf16LittleEndianBom_Decodes()
    {
        var wb = CsvFormat.Import(Cat([0xFF, 0xFE], Encoding.Unicode.GetBytes("x,日\r\n1,2")));
        Assert.Equal("x", wb.GetInput("A1"));
        Assert.Equal("日", wb.GetInput("B1"));
        Assert.Equal("2", wb.GetInput("B2"));
    }

    [Fact]
    public void Utf16BigEndianBom_Decodes()
    {
        var wb = CsvFormat.Import(Cat([0xFE, 0xFF], Encoding.BigEndianUnicode.GetBytes("x,日")));
        Assert.Equal("x", wb.GetInput("A1"));
        Assert.Equal("日", wb.GetInput("B1"));
    }

    [Fact]
    public void ValidUtf8WithoutBom_IsUtf8()
    {
        var wb = CsvFormat.Import(Encoding.UTF8.GetBytes("café,€"));
        Assert.Equal("café", wb.GetInput("A1"));
        Assert.Equal("€", wb.GetInput("B1"));
    }

    [Fact]
    public void InvalidUtf8_FallsBackToWindows1252()
    {
        // 0x80 alone is invalid UTF-8; in Windows-1252 it is the euro sign. 0xE9 is e-acute.
        var wb = CsvFormat.Import([0x80, (byte)',', 0xE9]);
        Assert.Equal("€", wb.GetInput("A1"));
        Assert.Equal("é", wb.GetInput("B1"));
    }

    [Fact]
    public void Utf8Bom_DecidesEvenWhenLaterBytesAreInvalid()
    {
        var wb = CsvFormat.Import(Cat([0xEF, 0xBB, 0xBF], [(byte)'a', 0xFF]));
        Assert.StartsWith("a", wb.GetInput("A1"));
    }

    [Fact]
    public void EmptyInput_GivesEmptyWorkbook()
    {
        Assert.Empty(CsvFormat.Import([]).GetUsedAddresses());
        Assert.Empty(CsvFormat.Import([0xEF, 0xBB, 0xBF]).GetUsedAddresses());
    }

    // ---- records and fields ----

    [Fact]
    public void MixedTerminators_SeparateRecords()
    {
        var wb = Imp("a\r\nb\nc\rd");
        Assert.Equal(["A1", "A2", "A3", "A4"], wb.GetUsedAddresses());
        Assert.Equal("d", wb.GetInput("A4"));
    }

    [Fact]
    public void FinalTerminator_DoesNotCreateARow()
    {
        Assert.Equal(["A1", "A2"], Imp("a\nb\n").GetUsedAddresses());
        Assert.Equal(["A1", "A2"], Imp("a\r\nb\r\n").GetUsedAddresses());
        Assert.Equal(["A1", "A2"], Imp("a\rb\r").GetUsedAddresses());
    }

    [Fact]
    public void QuotedFields_ContainDelimiterQuotesAndNewlines()
    {
        var wb = Imp("\"a,b\",\"say \"\"hi\"\"\",\"l1\r\nl2\"\n\"x\ny\",z");
        Assert.Equal("a,b", wb.GetInput("A1"));
        Assert.Equal("say \"hi\"", wb.GetInput("B1"));
        Assert.Equal("l1\r\nl2", wb.GetInput("C1"));
        Assert.Equal("x\ny", wb.GetInput("A2"));
        Assert.Equal("z", wb.GetInput("B2"));
    }

    [Fact]
    public void QuoteInsideUnquotedField_IsLiteral()
    {
        var wb = Imp("ab\"cd,5\"");
        Assert.Equal("ab\"cd", wb.GetInput("A1"));
        Assert.Equal("5\"", wb.GetInput("B1"));
    }

    [Fact]
    public void UnterminatedQuote_Throws()
    {
        Assert.Throws<GridlineFormatException>(() => Imp("a,\"unterminated\nb"));
        Assert.Throws<GridlineFormatException>(() => Imp("\"abc"));
    }

    [Fact]
    public void EmptyFields_AreBlank_AndRaggedRowsWork()
    {
        var wb = Imp("a,,c\n,\nx");
        Assert.Equal(["A1", "C1", "A3"], wb.GetUsedAddresses());
        Assert.Equal(CellValue.Blank, wb.GetValue("B1"));
    }

    [Fact]
    public void EqualsAndApostrophe_AreStoredAsText()
    {
        var wb = Imp("=1+2,'abc,'=x,\"=SUM(A1)\"");
        Assert.Equal("'=1+2", wb.GetInput("A1"));
        Assert.Equal(CellValue.FromText("=1+2"), wb.GetValue("A1"));
        Assert.Equal("''abc", wb.GetInput("B1"));
        Assert.Equal(CellValue.FromText("'abc"), wb.GetValue("B1"));
        Assert.Equal(CellValue.FromText("'=x"), wb.GetValue("C1"));
        Assert.Equal(CellValue.FromText("=SUM(A1)"), wb.GetValue("D1"));
    }

    [Fact]
    public void Fields_AreInterpretedPerSection43()
    {
        var wb = Imp("-5,5%,true,FALSE,1.5e3,hello");
        Assert.Equal(CellValue.FromNumber(-5), wb.GetValue("A1"));
        Assert.Equal(CellValue.FromNumber(0.05), wb.GetValue("B1"));
        Assert.Equal(CellValue.FromBoolean(true), wb.GetValue("C1"));
        Assert.Equal(CellValue.FromBoolean(false), wb.GetValue("D1"));
        Assert.Equal(CellValue.FromNumber(1500), wb.GetValue("E1"));
        Assert.Equal(CellValue.FromText("hello"), wb.GetValue("F1"));
    }

    [Fact]
    public void CustomDelimiters_Work()
    {
        var semi = Imp("a;b,c\n1;2", ';');
        Assert.Equal("b,c", semi.GetInput("B1"));
        Assert.Equal("2", semi.GetInput("B2"));
        var tab = Imp("a\tb;c\n\"x\ty\"\tz", '\t');
        Assert.Equal("b;c", tab.GetInput("B1"));
        Assert.Equal("x\ty", tab.GetInput("A2"));
        Assert.Equal("z", tab.GetInput("B2"));
    }

    [Theory]
    [InlineData('"')]
    [InlineData('\r')]
    [InlineData('\n')]
    public void InvalidDelimiter_ThrowsBeforeAnythingIsRead(char d)
    {
        Assert.Throws<ArgumentException>(() => CsvFormat.Import([], d));
        Assert.Throws<ArgumentException>(() => CsvFormat.Export(new Workbook(), d));
        // The file does not exist: ArgumentException (not an I/O error) proves the delimiter is checked first.
        Assert.Throws<ArgumentException>(() => CsvFormat.ImportFromPath(Path.Combine(_dir, "missing.csv"), d));
        string target = Path.Combine(_dir, "out.csv");
        Assert.Throws<ArgumentException>(() => CsvFormat.ExportToPath(new Workbook(), target, d));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Imported_IsLikeALoad()
    {
        var wb = Imp("1,2\n3,4");
        Assert.False(wb.IsDirty);
        Assert.False(wb.CanUndo);
        Assert.Empty(wb.GetNames());
        Assert.False(wb.Undo());
    }

    // ---- limits ----

    [Fact]
    public void RecordLimit_ExactBoundaryPasses_OneOverFails()
    {
        string ok = string.Join("\n", Enumerable.Repeat("1", 100_000));
        Assert.Equal(100_000, Imp(ok).GetUsedAddresses().Count);
        Assert.Throws<GridlineFormatException>(() => Imp(ok + "\n1"));
    }

    [Fact]
    public void FieldLimit_ExactBoundaryPasses_OneOverFails()
    {
        Assert.Equal(702, Imp(string.Join(",", Enumerable.Repeat("1", 702))).GetUsedAddresses().Count);
        Assert.Throws<GridlineFormatException>(() => Imp(string.Join(",", Enumerable.Repeat("1", 703))));
        Assert.Throws<GridlineFormatException>(() => Imp("1\n" + string.Join(",", Enumerable.Repeat("1", 703))));
    }

    [Fact]
    public void RecordLimit_IsEnforcedWhileParsing_SoAHugeFileStopsAtRecord100001()
    {
        // 1,000,000 records, then an unterminated quoted field. Parsing everything first would report the unterminated
        // quote; stopping at record 100,001 (§8.5, M-4) reports the record limit and never reaches it.
        var text = new StringBuilder(2_000_010);
        for (int i = 0; i < 1_000_000; i++) text.Append("1\n");
        text.Append("\"never closed");
        var ex = Assert.Throws<GridlineFormatException>(() => Imp(text.ToString()));
        Assert.Contains("more than 100000 records", ex.Message);
    }

    [Fact]
    public void FieldLimit_IsEnforcedWhileParsing_SoAHugeRecordStopsAtField703()
    {
        string wide = string.Join(",", Enumerable.Repeat("1", 50_000)) + ",\"never closed";
        var ex = Assert.Throws<GridlineFormatException>(() => Imp("a\n" + wide));
        Assert.Contains("Record 2 has more than 702 fields", ex.Message);
    }

    [Fact]
    public void Limits_AreCheckedBeforeAnUnterminatedQuoteBeyondThem_ButNotWithinThem()
    {
        string ok = string.Join("\n", Enumerable.Repeat("1", 99_999));
        var ex = Assert.Throws<GridlineFormatException>(() => Imp(ok + "\n\"open"));
        Assert.Contains("not terminated", ex.Message); // record 100,000 is within the limit, so it is parsed
    }

    [Fact]
    public void InputLength_ExactBoundaryPasses_OneOverFails()
    {
        string ok = new('x', 32_767);
        Assert.Equal(ok, Imp(ok).GetInput("A1"));
        Assert.Throws<GridlineFormatException>(() => Imp(new string('x', 32_768)));
        // A leading '=' gains an apostrophe, so the stored input is one longer than the field.
        Assert.Equal(32_767, Imp("=" + new string('x', 32_765)).GetInput("A1").Length);
        Assert.Throws<GridlineFormatException>(() => Imp("=" + new string('x', 32_766)));
    }

    // ---- export ----

    [Fact]
    public void Export_EmptyWorkbook_IsBomOnly()
    {
        Assert.Equal([0xEF, 0xBB, 0xBF], CsvFormat.Export(new Workbook()));
    }

    [Fact]
    public void Export_StartsWithBom_AndEndsEveryRecordWithCrlf()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("A2", "2");
        var bytes = CsvFormat.Export(wb);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("1\r\n2\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public void Export_ExtentIsA1ToMaxRowAndColumn_WithGaps()
    {
        var wb = new Workbook();
        wb.SetCell("B2", "x");
        wb.SetCell("D4", "y");
        Assert.Equal(",,,\r\n,x,,\r\n,,,\r\n,,,y\r\n", Exp(wb));
    }

    [Fact]
    public void Export_ValueKinds()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1.5");
        wb.SetCell("B1", "true");
        wb.SetCell("C1", "text");
        wb.SetCell("D1", "=1/0");
        wb.SetCell("E1", "5%");
        wb.SetCell("F1", "=0.1+0.2");
        wb.SetCell("G1", "-3");
        Assert.Equal("1.5,TRUE,text,#DIV/0!,0.05,0.30000000000000004,-3\r\n", Exp(wb));
    }

    [Fact]
    public void Export_ErrorValues_AreTheirCodes()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "=1+\"x\"");
        wb.SetCell("B1", "=NOSUCH(1)");
        wb.SetCell("C1", "=Undefined_Name");
        wb.SetCell("D1", "=MATCH(9,E1:E2,0)");
        Assert.Equal("#VALUE!,#NAME?,#NAME?,#N/A\r\n", Exp(wb));
    }

    [Fact]
    public void Export_FormulasAreExportedAsValues()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "2");
        wb.SetCell("B1", "=A1*3");
        wb.SetCell("C1", "=\"a\"&\"b\"");
        wb.SetCell("D1", "=A1>1");
        wb.SetCell("E1", "=\"\"");
        wb.SetCell("F1", "=Z9");
        Assert.Equal("2,6,ab,TRUE,,0\r\n", Exp(wb));
    }

    [Theory]
    [InlineData("a,b", true)]
    [InlineData("say \"hi\"", true)]
    [InlineData("l1\nl2", true)]
    [InlineData("l1\rl2", true)]
    [InlineData(" lead", true)]
    [InlineData("trail ", true)]
    [InlineData(" ", true)]
    [InlineData("in side", false)]
    [InlineData("semi;colon", false)]
    [InlineData("tab\there", false)]
    [InlineData("\tlead tab", false)]
    [InlineData("trail tab\t", false)]
    [InlineData(" lead nbsp", false)]
    [InlineData("trail nbsp ", false)]
    [InlineData("it's", false)]
    [InlineData("plain", false)]
    public void Export_QuotesIffATriggerIsPresent(string text, bool quoted)
    {
        var wb = new Workbook();
        wb.SetCell("A1", "'" + text);
        string expected = quoted ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        Assert.Equal(expected + "\r\n", Exp(wb));
    }

    [Fact]
    public void Export_CustomDelimiter_QuotesThatDelimiterOnly()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "'a;b");
        wb.SetCell("B1", "'c,d");
        wb.SetCell("C1", "'e\tf");
        Assert.Equal("\"a;b\";c,d;e\tf\r\n", Exp(wb, ';'));
        Assert.Equal("a;b\tc,d\t\"e\tf\"\r\n", Exp(wb, '\t'));
    }

    [Fact]
    public void Export_NonAscii_IsUtf8()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "日本€");
        Assert.Equal(Cat([0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes("日本€\r\n")), CsvFormat.Export(wb));
    }

    [Fact]
    public void Export_DoesNotChangeTheWorkbook()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        bool dirty = wb.IsDirty;
        CsvFormat.Export(wb);
        Assert.Equal(dirty, wb.IsDirty);
        Assert.Equal("1", wb.GetInput("A1"));
    }

    // ---- paths ----

    [Fact]
    public void ExportToPath_ThenImportFromPath_RoundTrips()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("B2", "=A1+1");
        string path = Path.Combine(_dir, "t.csv");
        CsvFormat.ExportToPath(wb, path);
        Assert.Equal(CsvFormat.Export(wb), File.ReadAllBytes(path));
        Assert.Equal("t.csv", Path.GetFileName(Assert.Single(Directory.GetFiles(_dir))));
        var back = CsvFormat.ImportFromPath(path);
        Assert.Equal(CellValue.FromNumber(2), back.GetValue("B2"));
        Assert.Equal("2", back.GetInput("B2"));
    }

    [Fact]
    public void ExportToPath_ReplacesExistingFile_AndFailureLeavesNothingBehind()
    {
        string path = Path.Combine(_dir, "t.csv");
        File.WriteAllText(path, "old");
        var wb = new Workbook();
        wb.SetCell("A1", "new");
        CsvFormat.ExportToPath(wb, path, ';');
        Assert.Equal("new\r\n", Encoding.UTF8.GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF'));

        string dirTarget = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(dirTarget);
        Assert.Throws<GridlineIOException>(() => CsvFormat.ExportToPath(wb, dirTarget));
        Assert.Equal(["sub", "t.csv"], Directory.GetFileSystemEntries(_dir).Select(p => Path.GetFileName(p)).Order().ToArray());
    }

    [Fact]
    public void ImportFromPath_MissingFile_IsMappedToIoException()
    {
        Assert.ThrowsAny<GridlineIOException>(() => CsvFormat.ImportFromPath(Path.Combine(_dir, "nope.csv")));
    }

    [Fact]
    public void ImportFromPath_BadContent_IsFormatException()
    {
        string path = Path.Combine(_dir, "bad.csv");
        File.WriteAllText(path, "\"open");
        Assert.Throws<GridlineFormatException>(() => CsvFormat.ImportFromPath(path));
    }
}
