using System.Text;
using Gridline.Core;
using Gridline.IO;

namespace Gridline.Cli.Tests;

/// <summary>import-csv / export-csv (§10.2, R72) and the Phase 3 script commands (§10.3, R73), in-process.</summary>
public sealed class CsvAndStructureCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridline-p3-" + Guid.NewGuid().ToString("N"));

    public CsvAndStructureCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static (int code, byte[] stdout, string stderr) Run(params string[] args)
    {
        var o = new MemoryStream();
        var e = new MemoryStream();
        int code = CliApp.Run(args, new StringReader(""), o, e);
        return (code, o.ToArray(), Encoding.UTF8.GetString(e.ToArray()));
    }

    private static (int code, string stdout) Script(string script)
    {
        var o = new MemoryStream();
        int code = CliApp.Run(["script"], new StringReader(script), o, new MemoryStream());
        byte[] b = o.ToArray();
        Assert.False(b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF, "stdout must have no BOM");
        string s = Encoding.UTF8.GetString(b);
        Assert.DoesNotContain('\r', s);
        return (code, s);
    }

    private static void AssertSingleErrorLine(string s)
    {
        Assert.StartsWith("error: ", s);
        Assert.EndsWith("\n", s);
        Assert.Equal(1, s.Count(c => c == '\n'));
        Assert.DoesNotContain('\r', s);
    }

    private string WriteText(string name, string text)
    {
        string path = P(name);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
        return path;
    }

    // ---- import-csv / export-csv ----

    [Fact]
    public void ImportCsv_Succeeds_PrintsNothing_AndWritesGridline()
    {
        string csv = WriteText("a.csv", "1,2\n=A1+B1,hello\n");
        string outp = P("a.gridline");
        var (code, o, e) = Run("import-csv", csv, outp);
        Assert.Equal(0, code);
        Assert.Empty(o);
        Assert.Empty(e);
        var wb = GridlineFile.LoadFromPath(outp);
        Assert.Equal("N:1", wb.GetValue("A1").ToCanonicalString());
        Assert.Equal("T:hello", wb.GetValue("B2").ToCanonicalString());
        Assert.Equal("'=A1+B1", wb.GetInput("A2")); // §8.5 injection guard
    }

    [Theory]
    [InlineData(";", "1;2\n")]
    [InlineData("\t", "1\t2\n")]
    public void ImportCsv_Delimiter(string delim, string text)
    {
        string csv = WriteText("d.csv", text);
        string outp = P("d.gridline");
        var (code, o, e) = Run("import-csv", csv, outp, "--delimiter", delim);
        Assert.Equal(0, code);
        Assert.Empty(o);
        Assert.Empty(e);
        Assert.Equal("N:2", GridlineFile.LoadFromPath(outp).GetValue("B1").ToCanonicalString());
    }

    [Theory]
    [InlineData(";")]
    [InlineData("\t")]
    public void ExportCsv_Delimiter_AndRoundTrip(string delim)
    {
        string g = P("g.gridline");
        GridlineFile.SaveToPath(Workbook.FromCells([new("A1", "1"), new("B1", "a b"), new("A2", "=A1+1")]), g);
        string csv = P("o.csv");
        var (code, o, e) = Run("export-csv", g, csv, "--delimiter", delim);
        Assert.Equal(0, code);
        Assert.Empty(o);
        Assert.Empty(e);
        Assert.Equal("1" + delim + "a b\r\n2" + delim + "\r\n", Encoding.UTF8.GetString(File.ReadAllBytes(csv)[3..])); // the rectangle is two columns wide

        string back = P("back.gridline");
        Assert.Equal(0, Run("import-csv", csv, back, "--delimiter", delim).code);
        Assert.Equal("N:2", GridlineFile.LoadFromPath(back).GetValue("A2").ToCanonicalString());
        string csv2 = P("o2.csv");
        Assert.Equal(0, Run("export-csv", back, csv2, "--delimiter", delim).code);
        Assert.Equal(File.ReadAllBytes(csv), File.ReadAllBytes(csv2));
    }

    [Fact]
    public void ExportCsv_DefaultComma_WritesBomAndCrlf()
    {
        string g = P("g.gridline");
        GridlineFile.SaveToPath(Workbook.FromCells([new("A1", "x,y")]), g);
        string csv = P("o.csv");
        Assert.Equal(0, Run("export-csv", g, csv).code);
        byte[] bytes = File.ReadAllBytes(csv);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("\"x,y\"\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("\"")]
    [InlineData("\r")]
    [InlineData("\n")]
    public void BadDelimiter_Exit1_BeforeAnyIo(string delim)
    {
        string outp = P("x.gridline");
        var (c1, o1, e1) = Run("import-csv", P("missing.csv"), outp, "--delimiter", delim); // missing file: usage wins
        Assert.Equal(1, c1);
        Assert.Empty(o1);
        AssertSingleErrorLine(e1);
        Assert.False(File.Exists(outp));
        var (c2, o2, e2) = Run("export-csv", P("missing.gridline"), P("x.csv"), "--delimiter", delim);
        Assert.Equal(1, c2);
        Assert.Empty(o2);
        AssertSingleErrorLine(e2);
        Assert.False(File.Exists(P("x.csv")));
    }

    [Theory]
    [InlineData("import-csv")]
    [InlineData("export-csv")]
    public void UsageErrors_Exit1(string cmd)
    {
        foreach (string[] a in new[]
        {
            new[] { cmd }, new[] { cmd, "a" }, new[] { cmd, "a", "b", "c" }, new[] { cmd, "a", "b", "--delimiter" },
            new[] { cmd, "a", "b", "--delimiter", ";", "x" }, new[] { cmd, "a", "b", "--out", ";" },
            new[] { cmd, "", "b" }, new[] { cmd, "a", "" },
        })
        {
            var (code, o, e) = Run(a);
            Assert.Equal(1, code);
            Assert.Empty(o);
            AssertSingleErrorLine(e);
        }
    }

    [Fact]
    public void Import_MissingCsv_Exit2()
    {
        var (code, o, e) = Run("import-csv", P("nope.csv"), P("x.gridline"));
        Assert.Equal(2, code);
        Assert.Empty(o);
        AssertSingleErrorLine(e);
        Assert.False(File.Exists(P("x.gridline")));
    }

    [Fact]
    public void Import_CsvFormatError_Exit3()
    {
        string csv = WriteText("bad.csv", "a,\"unterminated\n");
        var (code, o, e) = Run("import-csv", csv, P("x.gridline"));
        Assert.Equal(3, code);
        Assert.Empty(o);
        AssertSingleErrorLine(e);
        Assert.False(File.Exists(P("x.gridline")));
    }

    [Fact]
    public void Import_OutputIoError_Exit2()
    {
        string csv = WriteText("a.csv", "1\n");
        var (code, o, e) = Run("import-csv", csv, Path.Combine(_dir, "no-such-dir", "x.gridline"));
        Assert.Equal(2, code);
        Assert.Empty(o);
        AssertSingleErrorLine(e);
    }

    [Fact]
    public void Export_MissingInput_Exit2()
    {
        var (code, o, e) = Run("export-csv", P("nope.gridline"), P("x.csv"));
        Assert.Equal(2, code);
        Assert.Empty(o);
        AssertSingleErrorLine(e);
        Assert.False(File.Exists(P("x.csv")));
    }

    [Fact]
    public void Export_GridlineFormatError_Exit3()
    {
        string bad = WriteText("bad.gridline", "this is not a gridline file");
        var (code, o, e) = Run("export-csv", bad, P("x.csv"));
        Assert.Equal(3, code);
        Assert.Empty(o);
        AssertSingleErrorLine(e);
        Assert.False(File.Exists(P("x.csv")));
    }

    [Fact]
    public void Export_OutputIoError_Exit2()
    {
        string g = P("g.gridline");
        GridlineFile.SaveToPath(new Workbook(), g);
        var (code, o, e) = Run("export-csv", g, Path.Combine(_dir, "no-such-dir", "x.csv"));
        Assert.Equal(2, code);
        Assert.Empty(o);
        AssertSingleErrorLine(e);
    }

    [Fact]
    public void UnknownCommand_UsageMentionsCsvCommands()
    {
        var (code, _, e) = Run("frob");
        Assert.Equal(1, code);
        Assert.Contains("import-csv", e);
        Assert.Contains("export-csv", e);
    }

    // ---- script structure commands ----

    [Fact]
    public void InsertRows_ShiftsCells_AndUndoRestores()
    {
        var (code, o) = Script("SET A1 1\nSET A2 =A1+1\nINSERTROWS 2 3\nDUMP\nUNDO\nDUMP\nDIRTY\n");
        Assert.Equal(0, code);
        Assert.Equal("A1\tN:1\nA5\tN:2\nOK\nA1\tN:1\nA2\tN:2\nTRUE\n", o);
    }

    [Fact]
    public void DeleteRows_RemovesCells_AndUndoRestores()
    {
        var (code, o) = Script("SET A1 1\nSET A2 2\nSET A3 =A2\nDELETEROWS 1 1\nDUMP\nUNDO\nINPUT A3\n");
        Assert.Equal(0, code);
        Assert.Equal("A1\tN:2\nA2\tN:2\nOK\nA3\t=A2\n", o);
    }

    [Fact]
    public void InsertAndDeleteCols_LettersCaseInsensitive_AndUndo()
    {
        var (code, o) = Script("SET A1 1\nSET B1 2\ninsertcols b 2\nDUMP\nUNDO\nDeleteCols a 1\nDUMP\nUNDO\nDUMP\n");
        Assert.Equal(0, code);
        Assert.Equal("A1\tN:1\nD1\tN:2\nOK\nA1\tN:2\nOK\nA1\tN:1\nB1\tN:2\n", o);
    }

    [Fact]
    public void LeadingZeros_AreAccepted()
    {
        var (code, o) = Script("SET A1 1\nINSERTROWS 01 001\nDUMP\n");
        Assert.Equal(0, code);
        Assert.Equal("A2\tN:1\n", o);
    }

    [Fact]
    public void ZZ_IsAccepted()
    {
        var (code, o) = Script("DELETECOLS zz 1\nDELETECOLS ZZ 1\nINSERTCOLS ZZ 1\n");
        Assert.Equal(0, code);
        Assert.Equal("", o);
    }

    [Theory]
    [InlineData("INSERTROWS")]
    [InlineData("INSERTROWS 1")]
    [InlineData("INSERTROWS 1 1 1")]
    [InlineData("INSERTROWS  1 1")]
    [InlineData("INSERTROWS 1  1")]
    [InlineData("INSERTROWS +1 1")]
    [InlineData("INSERTROWS 1 -1")]
    [InlineData("INSERTROWS 1.0 1")]
    [InlineData("INSERTROWS A 1")]
    [InlineData("INSERTROWS 1 0x1")]
    [InlineData("INSERTROWS ١ 1")]
    [InlineData("INSERTROWS 99999999999 1")]
    [InlineData("INSERTROWS 1 2147483648")]
    [InlineData("DELETEROWS 1")]
    [InlineData("DELETEROWS 0 1")]
    [InlineData("DELETEROWS 1 0")]
    [InlineData("INSERTROWS 100001 1")]
    [InlineData("INSERTCOLS")]
    [InlineData("INSERTCOLS 1 1")]
    [InlineData("INSERTCOLS AAA 1")]
    [InlineData("INSERTCOLS A1 1")]
    [InlineData("INSERTCOLS $A 1")]
    [InlineData("INSERTCOLS A")]
    [InlineData("INSERTCOLS A 1 1")]
    [InlineData("INSERTCOLS A -1")]
    [InlineData("INSERTCOLS A 0")]
    [InlineData("INSERTCOLS A 2147483648")]
    [InlineData("DELETECOLS")]
    [InlineData("DELETECOLS Z 703")]
    [InlineData("DELETECOLS ZZ 2")]
    [InlineData("DELETECOLS Ä 1")]
    public void BadStructureArguments_PrintErr_AndChangeNothing(string line)
    {
        var (code, o) = Script("SET A1 1\nSET B1 2\n" + line + "\nDUMP\nUNDO\nDUMP\n");
        Assert.Equal(4, code);
        string[] lines = o.Split('\n');
        Assert.StartsWith("ERR\t", lines[0]);
        Assert.Equal(1, lines.Count(l => l.StartsWith("ERR\t")));
        Assert.Equal(["A1\tN:1", "B1\tN:2", "OK", "A1\tN:1", ""], lines[1..]); // UNDO undid SET B1: no structure step existed
    }

    [Fact]
    public void InvalidOperation_FromApi_IsSingleLineErr()
    {
        var (code, o) = Script("SET A100000 1\nINSERTROWS 1 1\nGET A100000\n");
        Assert.Equal(4, code);
        string[] lines = o.Split('\n');
        Assert.StartsWith("ERR\t", lines[0]);
        Assert.Equal("A100000\tN:1", lines[1]);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Name_Names_Unname_AndUndo()
    {
        var (code, o) = Script(
            "NAME Zed $A$1\nNAME alpha $b$2:$a$1\nNAMES\nSET A1 5\nSET C1 =Zed*2\nGET C1\nUNNAME zed\nNAMES\nUNDO\nNAMES\nUNDO\nUNDO\nUNDO\nNAMES\n");
        Assert.Equal(0, code);
        Assert.Equal(
            "NAME\talpha\t$A$1:$B$2\nNAME\tZed\t$A$1\n" + "C1\tN:10\n" + "NAME\talpha\t$A$1:$B$2\n"
            + "OK\nNAME\talpha\t$A$1:$B$2\nNAME\tZed\t$A$1\n" + "OK\nOK\nOK\nNAME\tZed\t$A$1\n", o);
    }

    [Fact]
    public void Names_Empty_PrintsNothing()
    {
        var (code, o) = Script("NAMES\n");
        Assert.Equal(0, code);
        Assert.Equal("", o);
    }

    [Fact]
    public void Names_AreSortedOrdinalIgnoreCase()
    {
        var (code, o) = Script("NAME b $A$1\nNAME A $A$2\nNAME _c $A$3\nNAME Bz $A$4\nNAMES\n");
        Assert.Equal(0, code);
        Assert.Equal("NAME\tA\t$A$2\nNAME\tb\t$A$1\nNAME\tBz\t$A$4\nNAME\t_c\t$A$3\n", o); // '_' sorts after letters (OrdinalIgnoreCase upper-cases)
    }

    [Theory]
    [InlineData("NAME")]
    [InlineData("NAME x")]
    [InlineData("NAME x $A$1 y")]
    [InlineData("NAME  x $A$1")]
    [InlineData("NAME x  $A$1")]
    [InlineData("NAME x A1")]
    [InlineData("NAME x #REF!")]
    [InlineData("NAME A1 $A$1")]
    [InlineData("NAME 1x $A$1")]
    [InlineData("NAME x $A$0")]
    [InlineData("UNNAME")]
    [InlineData("UNNAME a b")]
    [InlineData("UNNAME nothere")]
    [InlineData("NAMES x")]
    public void BadNameCommands_PrintErr_AndChangeNothing(string line)
    {
        var (code, o) = Script("NAME keep $A$1\n" + line + "\nNAMES\n");
        Assert.Equal(4, code);
        string[] lines = o.Split('\n');
        Assert.StartsWith("ERR\t", lines[0]);
        Assert.Equal(["NAME\tkeep\t$A$1", ""], lines[1..]);
    }

    [Fact]
    public void RedefiningSameName_IsNoOp_NoUndoStep()
    {
        var (code, o) = Script("NAME x $A$1\nNAME x $A$1\nUNDO\nNAMES\nUNDO\n");
        Assert.Equal(0, code);
        Assert.Equal("OK\nNOOP\n", o);
    }

    [Theory]
    [InlineData("INSERTROWS 1 1")]
    [InlineData("DELETEROWS 1 1")]
    [InlineData("INSERTCOLS A 1")]
    [InlineData("DELETECOLS A 1")]
    [InlineData("NAME y $A$1")]
    [InlineData("UNNAME x")]
    [InlineData("NAMES")]
    public void InsideBeginBlock_IsErr_AndBlockContinues(string line)
    {
        var (code, o) = Script("NAME x $A$1\nBEGIN\nSET A1 7\n" + line + "\nEND\nGET A1\nNAMES\n");
        Assert.Equal(4, code);
        Assert.Equal("ERR\tcommand not allowed inside BEGIN block\nA1\tN:7\nNAME\tx\t$A$1\n", o);
    }

    [Fact]
    public void Err_ContinuesAndExits4()
    {
        var (code, o) = Script("INSERTROWS x 1\nSET A1 1\nGET A1\n");
        Assert.Equal(4, code);
        Assert.StartsWith("ERR\t", o);
        Assert.EndsWith("A1\tN:1\n", o);
    }

    [Fact]
    public void SaveAfterNamesAndStructureEdits_Persists()
    {
        string f = P("n.gridline");
        var (code, _) = Script($"SET A1 1\nSET A2 2\nNAME rng $A$1:$A$2\nINSERTROWS 1 1\nSAVE {f}\n");
        Assert.Equal(0, code);
        var wb = GridlineFile.LoadFromPath(f);
        Assert.Equal("$A$2:$A$3", wb.GetNames().Single().Value);
        Assert.Equal("N:1", wb.GetValue("A2").ToCanonicalString());
    }
}
