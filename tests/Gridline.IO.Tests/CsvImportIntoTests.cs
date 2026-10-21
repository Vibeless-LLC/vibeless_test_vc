using System.Text;
using Gridline.Core;

namespace Gridline.IO.Tests;

/// <summary>CSV ImportInto and per-sheet Export (CR1.6, R76, §8.5).</summary>
public sealed class CsvImportIntoTests
{
    private static MemoryStream S(string text) => new(Encoding.UTF8.GetBytes(text));
    private static string Exp(Workbook wb, string? sheet = null, char d = ',') =>
        Encoding.UTF8.GetString(CsvFormat.Export(wb, d, sheet)).TrimStart('﻿');

    [Fact]
    public void AppendsASheetWithTheSection85Mapping_AndKeepsTheActiveSheet()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "keep");
        wb.MarkSaved();
        CsvFormat.ImportInto(wb, "Data", S("n,t,f,e\r\n1.5,\"a,b\",=1+1,'x\r\n,TRUE,,-3"));
        Assert.Equal(new[] { "Sheet1", "Data" }, wb.SheetNames);
        Assert.Equal("Sheet1", wb.ActiveSheet);
        Assert.Equal("keep", wb.GetInput("A1"));
        Assert.Equal("1.5", wb.GetInput("Data!A2"));
        Assert.Equal(1.5, wb.GetValue("Data!A2").Number);
        Assert.Equal("a,b", wb.GetValue("Data!B2").Text);
        Assert.Equal("'=1+1", wb.GetInput("Data!C2")); // no formula injection
        Assert.Equal("=1+1", wb.GetValue("Data!C2").Text);
        Assert.Equal("''x", wb.GetInput("Data!D2"));
        Assert.Equal("", wb.GetInput("Data!A3"));
        Assert.True(wb.GetValue("Data!B3").Boolean);
        Assert.Equal(-3, wb.GetValue("Data!D3").Number);
        Assert.True(wb.IsDirty);
    }

    [Fact]
    public void Encodings_AreDetectedAsInImport()
    {
        var wb = new Workbook();
        byte[] utf16 = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("x,日\r\n1,2")];
        CsvFormat.ImportInto(wb, "U", new MemoryStream(utf16));
        CsvFormat.ImportInto(wb, "W", new MemoryStream([0x63, 0x61, 0x66, 0xE9])); // "café" in Windows-1252
        Assert.Equal("日", wb.GetInput("U!B1"));
        Assert.Equal("café", wb.GetInput("W!A1"));
    }

    [Fact]
    public void FormulaWrittenBeforeTheImport_ResolvesAfterIt_AndEventIsRaisedOnce()
    {
        var wb = new Workbook();
        var events = new List<string[]>();
        wb.CellsChanged += (_, e) => events.Add(e.Addresses.ToArray());
        wb.SetCell("A1", "=Data!A1+1");
        Assert.Equal("#REF!", wb.GetValue("A1").Error);
        events.Clear();
        CsvFormat.ImportInto(wb, "Data", S("41"));
        Assert.Equal(42, wb.GetValue("A1").Number);
        var e = Assert.Single(events);
        Assert.Equal(new[] { "Sheet1!A1", "Data!A1" }, e);
    }

    [Fact]
    public void OneUndoStep_RemovesTheSheet_RedoRestoresIt()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "=Data!B1");
        CsvFormat.ImportInto(wb, "Data", S("1,2\r\n3,4"));
        Assert.Equal(2, wb.GetValue("A1").Number);
        Assert.True(wb.Undo());
        Assert.Equal(new[] { "Sheet1" }, wb.SheetNames);
        Assert.Equal("#REF!", wb.GetValue("A1").Error);
        Assert.True(wb.Redo());
        Assert.Equal(new[] { "Sheet1", "Data" }, wb.SheetNames);
        Assert.Equal("4", wb.GetInput("Data!B2"));
        Assert.Equal(2, wb.GetValue("A1").Number);
        Assert.True(wb.Undo());
        Assert.True(wb.Undo()); // the SetCell
        Assert.False(wb.Undo());
        Assert.False(wb.IsDirty);
    }

    [Fact]
    public void UndoOfAnActiveImportedSheet_MovesActivation()
    {
        var wb = new Workbook();
        CsvFormat.ImportInto(wb, "Data", S("1"));
        wb.ActiveSheet = "Data";
        wb.Undo();
        Assert.Equal("Sheet1", wb.ActiveSheet);
    }

    [Fact]
    public void SemicolonDelimiter_ThroughOptions()
    {
        var wb = new Workbook();
        CsvFormat.ImportInto(wb, "S", S("a;b,c\r\n\"x;y\";2"), new CsvOptions { Delimiter = ';' });
        Assert.Equal("b,c", wb.GetInput("S!B1"));
        Assert.Equal("x;y", wb.GetInput("S!A2"));
        Assert.Equal("2", wb.GetInput("S!B2"));
    }

    [Fact]
    public void EmptyStream_AddsAnEmptySheet()
    {
        var wb = new Workbook();
        CsvFormat.ImportInto(wb, "E", S(""));
        Assert.Equal(new[] { "Sheet1", "E" }, wb.SheetNames);
        Assert.True(wb.IsDirty);
    }

    private static void AssertUnchanged(Workbook wb)
    {
        Assert.Equal(new[] { "Sheet1" }, wb.SheetNames);
        Assert.False(wb.IsDirty);
        Assert.False(wb.Undo());
        Assert.Empty(wb.GetUsedAddresses());
    }

    [Fact]
    public void ExistingName_CaseInsensitive_ChangesNothing()
    {
        var wb = new Workbook();
        Assert.Throws<ArgumentException>(() => CsvFormat.ImportInto(wb, "sheet1", S("1")));
        AssertUnchanged(wb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a:b")]
    [InlineData("[x]")]
    [InlineData("'q")]
    public void InvalidName_ChangesNothing(string name)
    {
        var wb = new Workbook();
        Assert.Throws<ArgumentException>(() => CsvFormat.ImportInto(wb, name, S("1")));
        AssertUnchanged(wb);
    }

    [Theory]
    [InlineData('"')]
    [InlineData('\r')]
    [InlineData('\n')]
    public void BadDelimiter_ChangesNothing(char d)
    {
        var wb = new Workbook();
        Assert.Throws<ArgumentException>(() => CsvFormat.ImportInto(wb, "D", S("1"), new CsvOptions { Delimiter = d }));
        AssertUnchanged(wb);
    }

    [Fact]
    public void FormatErrors_ChangeNothing()
    {
        var wb = new Workbook();
        Assert.Throws<GridlineFormatException>(() => CsvFormat.ImportInto(wb, "D", S("a,\"unterminated")));
        Assert.Throws<GridlineFormatException>(() => CsvFormat.ImportInto(wb, "D", S(string.Join(",", Enumerable.Repeat("x", 703)))));
        Assert.Throws<GridlineFormatException>(() => CsvFormat.ImportInto(wb, "D", S(new string('x', 32_768))));
        Assert.Throws<GridlineFormatException>(() => CsvFormat.ImportInto(wb, "D", S("=" + new string('x', 32_767)))); // 32,768 once stored
        AssertUnchanged(wb);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var wb = new Workbook();
        Assert.Throws<ArgumentNullException>(() => CsvFormat.ImportInto(null!, "D", S("1")));
        Assert.Throws<ArgumentNullException>(() => CsvFormat.ImportInto(wb, null!, S("1")));
        Assert.Throws<ArgumentNullException>(() => CsvFormat.ImportInto(wb, "D", null!));
        AssertUnchanged(wb);
    }

    [Fact]
    public void FullWorkbook_255Sheets_Throws_AndChangesNothing()
    {
        var wb = new Workbook();
        for (int i = 2; i <= 255; i++) wb.AddSheet("S" + i);
        wb.MarkSaved();
        Assert.Throws<InvalidOperationException>(() => CsvFormat.ImportInto(wb, "Extra", S("1")));
        Assert.Equal(255, wb.SheetNames.Count);
        Assert.False(wb.IsDirty);
    }

    [Fact]
    public void ImportThenExport_RoundTripsTheSheet()
    {
        var wb = new Workbook();
        string csv = "a,\"b,c\",\" d\"\r\n1,\"x\"\"y\",\r\n";
        CsvFormat.ImportInto(wb, "Data", S(csv));
        Assert.Equal(csv, Exp(wb, "Data"));
    }

    [Fact]
    public void Import_StillCreatesSheet1()
    {
        var wb = CsvFormat.Import(Encoding.UTF8.GetBytes("1,2"));
        Assert.Equal(new[] { "Sheet1" }, wb.SheetNames);
        Assert.False(wb.IsDirty);
    }

    [Fact]
    public void Export_NullIsTheActiveSheet_NamedSheetIsCaseInsensitive()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "one");
        wb.AddSheet("Data");
        wb.SetCell("Data!A1", "=Sheet1!A1");
        wb.SetCell("Data!B2", "2");
        Assert.Equal("one\r\n", Exp(wb));
        Assert.Equal("one,\r\n,2\r\n", Exp(wb, "dATA"));
        wb.ActiveSheet = "Data";
        Assert.Equal("one,\r\n,2\r\n", Exp(wb));
        Assert.Equal("one\r\n", Exp(wb, "SHEET1"));
        Assert.Equal("one;\r\n;2\r\n", Exp(wb, "Data", ';'));
    }

    [Fact]
    public void Export_EmptyNonActiveSheet_IsBomOnly()
    {
        var wb = new Workbook();
        wb.AddSheet("Empty");
        Assert.Equal(Encoding.UTF8.Preamble.ToArray(), CsvFormat.Export(wb, ',', "Empty"));
    }

    [Fact]
    public void Export_UnknownSheet_Throws()
    {
        var wb = new Workbook();
        Assert.Throws<ArgumentException>(() => CsvFormat.Export(wb, ',', "Nope"));
        Assert.Throws<ArgumentException>(() => CsvFormat.Export(wb, ',', ""));
        Assert.Throws<ArgumentException>(() => CsvFormat.Export(wb, ',', "Sheet1!A1"));
    }

    [Fact]
    public void ExportToPath_WritesTheNamedSheet()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridline-csv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var wb = new Workbook();
            CsvFormat.ImportInto(wb, "Data", S("p,q"));
            string path = Path.Combine(dir, "d.csv");
            CsvFormat.ExportToPath(wb, path, ',', "Data");
            Assert.Equal("p,q\r\n", Encoding.UTF8.GetString(File.ReadAllBytes(path)).TrimStart('﻿'));
            Assert.Throws<ArgumentException>(() => CsvFormat.ExportToPath(wb, Path.Combine(dir, "x.csv"), ',', "Nope"));
            Assert.False(File.Exists(Path.Combine(dir, "x.csv")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
