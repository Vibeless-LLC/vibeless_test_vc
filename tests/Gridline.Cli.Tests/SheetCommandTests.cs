using System.Text;
using Gridline.Core;
using Gridline.IO;

namespace Gridline.Cli.Tests;

/// <summary>
/// CR1.7 (§10.1-§10.3, R87): `--sheet` on import-csv/export-csv, qualified addresses in eval/set/GET/INPUT/SET/SETX/PASTE,
/// DUMP and eval --all per the GetUsedAddresses rule, and the sheet script commands. In-process through
/// <see cref="CliApp.Run"/>, plus process-level runs of the built gridline.dll at the end.
/// </summary>
public sealed class SheetCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridline-cr17-" + Guid.NewGuid().ToString("N"));

    public SheetCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private static (int code, string stdout, string stderr) Run(params string[] args)
    {
        var o = new MemoryStream();
        var e = new MemoryStream();
        int code = CliApp.Run(args, new StringReader(""), o, e);
        byte[] b = o.ToArray();
        Assert.False(b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF, "stdout must have no BOM");
        string s = Encoding.UTF8.GetString(b);
        Assert.DoesNotContain('\r', s);
        return (code, s, Encoding.UTF8.GetString(e.ToArray()));
    }

    private static (int code, string stdout) Script(string script, params string[] extra)
    {
        var (code, o, e) = RunScript(script, extra);
        Assert.Empty(e);
        return (code, o);
    }

    private static (int code, string stdout, string stderr) RunScript(string script, params string[] extra)
    {
        var o = new MemoryStream();
        var e = new MemoryStream();
        int code = CliApp.Run(["script", .. extra], new StringReader(script), o, e);
        string s = Encoding.UTF8.GetString(o.ToArray());
        Assert.DoesNotContain('\r', s);
        return (code, s, Encoding.UTF8.GetString(e.ToArray()));
    }

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    private static void AssertSingleErrorLine(string s)
    {
        Assert.StartsWith("error: ", s);
        Assert.EndsWith("\n", s);
        Assert.Equal(1, s.Count(c => c == '\n'));
    }

    /// <summary>Sheet1 (A1 = 21), Data (B2 = Sheet1!A1*2, A1 = text), 'My Sheet' (C3 = 5); Sheet1 active.</summary>
    private string MultiSheetFile(string name = "multi.gridline")
    {
        var wb = new Workbook();
        wb.SetCell("A1", "21");
        wb.AddSheet("Data");
        wb.AddSheet("My Sheet");
        wb.SetCell("Data!B2", "=Sheet1!A1*2");
        wb.SetCell("Data!A1", "hi");
        wb.SetCell("'My Sheet'!C3", "5");
        string path = P(name);
        GridlineFile.SaveToPath(wb, path);
        return path;
    }

    private string SingleSheetFile()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.SetCell("B1", "=A1+1");
        string path = P("single.gridline");
        GridlineFile.SaveToPath(wb, path);
        return path;
    }

    private string WriteText(string name, string text)
    {
        string path = P(name);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
        return path;
    }

    // ---- eval ----

    [Fact]
    public void Eval_QualifiedLowercaseAndQuoted_EchoCanonical()
    {
        string f = MultiSheetFile();
        var (code, o, e) = Run("eval", f, "data!b2", "DATA!$A$1", "'my sheet'!c3", "'Data'!B2", "a1", "Sheet1!A1");
        Assert.Equal(0, code);
        Assert.Empty(e);
        Assert.Equal(Lines(
            "Data!B2\tN:42",
            "Data!A1\tT:hi",
            "'My Sheet'!C3\tN:5",
            "Data!B2\tN:42",        // a redundantly quoted prefix prints unquoted
            "A1\tN:21",             // unqualified stays unqualified (the active sheet)
            "Sheet1!A1\tN:21"), o);
    }

    [Fact]
    public void Eval_UnknownSheet_Exit5_NoOutput()
    {
        string f = MultiSheetFile();
        var (code, o, e) = Run("eval", f, "A1", "Nope!A1");
        Assert.Equal(ExitCodes.InvalidAddress, code);
        Assert.Empty(o); // every address is resolved before any output
        AssertSingleErrorLine(e);
        Assert.Contains("unknown sheet 'Nope'", e);
    }

    [Theory]
    [InlineData("My Sheet!A1")]   // a prefix needing quotes, unquoted (R82)
    [InlineData("2024!A1")]
    [InlineData("'Data!A1")]      // unterminated quote
    [InlineData("'Data' !A1")]
    [InlineData("Data!")]
    [InlineData("Data!A1:B2")]
    [InlineData("''!A1")]
    public void Eval_MalformedQualifiedAddress_Exit5(string addr)
    {
        string f = MultiSheetFile();
        var (code, o, e) = Run("eval", f, addr);
        Assert.Equal(ExitCodes.InvalidAddress, code);
        Assert.Empty(o);
        AssertSingleErrorLine(e);
    }

    [Fact]
    public void Eval_All_SingleSheet_Unqualified()
    {
        var (code, o, _) = Run("eval", SingleSheetFile(), "--all");
        Assert.Equal(0, code);
        Assert.Equal(Lines("A1\tN:1", "B1\tN:2"), o);
    }

    [Fact]
    public void Eval_All_MultiSheet_QualifiedInSheetOrder()
    {
        var (code, o, _) = Run("eval", MultiSheetFile(), "--all");
        Assert.Equal(0, code);
        Assert.Equal(Lines("Sheet1!A1\tN:21", "Data!A1\tT:hi", "Data!B2\tN:42", "'My Sheet'!C3\tN:5"), o);
    }

    [Fact]
    public void Set_QualifiedAddress_SavesToThatSheet()
    {
        string f = MultiSheetFile();
        string outp = P("set.gridline");
        var (code, o, e) = Run("set", f, "'my sheet'!d4", "=Data!B2+1", "--out", outp);
        Assert.Equal(0, code);
        Assert.Empty(o);
        Assert.Empty(e);
        var wb = GridlineFile.LoadFromPath(outp);
        Assert.Equal("=Data!B2+1", wb.GetInput("'My Sheet'!D4"));
        Assert.Equal("N:43", wb.GetValue("'My Sheet'!D4").ToCanonicalString());
        Assert.Equal("", wb.GetInput("Sheet1!D4"));
    }

    [Fact]
    public void Set_UnknownSheet_Exit5_NothingWritten()
    {
        string f = MultiSheetFile();
        byte[] before = File.ReadAllBytes(f);
        var (code, _, e) = Run("set", f, "Nope!A1", "1");
        Assert.Equal(ExitCodes.InvalidAddress, code);
        AssertSingleErrorLine(e);
        Assert.Equal(before, File.ReadAllBytes(f));
    }

    // ---- import-csv / export-csv --sheet ----

    [Fact]
    public void ImportCsv_Sheet_NamesTheOnlySheet_ThenEvalQualified()
    {
        string csv = WriteText("a.csv", "1,2\nx,3\n");
        string outp = P("a.gridline");
        var (code, o, e) = Run("import-csv", csv, outp, "--sheet", "Data");
        Assert.Equal(0, code);
        Assert.Empty(o);
        Assert.Empty(e);

        var wb = GridlineFile.LoadFromPath(outp);
        Assert.Equal(["Data"], wb.SheetNames);
        Assert.Equal("Data", wb.ActiveSheet);
        Assert.Empty(wb.GetNames());

        // Byte-identical to the plain import except for the sheet name (the file is clean: no Sheet1, no extra state).
        string plain = P("plain.gridline");
        Assert.Equal(0, Run("import-csv", csv, plain).code);
        Assert.Equal(File.ReadAllText(plain).Replace("\"Sheet1\"", "\"Data\""), File.ReadAllText(outp));

        var (c2, o2, _) = Run("eval", outp, "Data!A1", "data!b2", "A2");
        Assert.Equal(0, c2);
        Assert.Equal(Lines("Data!A1\tN:1", "Data!B2\tN:3", "A2\tT:x"), o2);
    }

    [Fact]
    public void ImportCsv_Sheet_QuotedNameAndOptionsInEitherOrder()
    {
        string csv = WriteText("s.csv", "1;2\n");
        string outp = P("s.gridline");
        var (code, _, e) = Run("import-csv", csv, outp, "--sheet", "My Data", "--delimiter", ";");
        Assert.Equal(0, code);
        Assert.Empty(e);
        var wb = GridlineFile.LoadFromPath(outp);
        Assert.Equal(["My Data"], wb.SheetNames);
        Assert.Equal("N:2", wb.GetValue("B1").ToCanonicalString());

        var (c2, o2, _) = Run("eval", outp, "'my data'!B1");
        Assert.Equal(0, c2);
        Assert.Equal("'My Data'!B1\tN:2\n", o2);
    }

    [Theory]
    [InlineData("import-csv")]
    [InlineData("export-csv")]
    public void CsvSheet_BadArguments_Exit1_BeforeAnyFileIsTouched(string command)
    {
        string outp = P("never.out");
        string missing = P("missing.in"); // the argument error wins over the missing input (exit 2)
        foreach (string[] tail in new[]
        {
            new[] { "--sheet" },
            new[] { "--sheet", "" },
            new[] { "--sheet", "a:b" },
            new[] { "--sheet", "'x" },
            new[] { "--sheet", new string('s', 32) },
            new[] { "--sheet", "A", "--sheet", "B" },
            new[] { "--sheet", "A", "--bogus" },
        })
        {
            var (code, o, e) = Run([command, missing, outp, .. tail]);
            Assert.True(code == ExitCodes.Usage, string.Join(" ", tail) + " → " + code);
            Assert.Empty(o);
            AssertSingleErrorLine(e);
            Assert.False(File.Exists(outp));
        }
    }

    [Fact]
    public void ExportCsv_Sheet_SelectsTheSheet_CaseInsensitive()
    {
        string f = MultiSheetFile();
        string outp = P("data.csv");
        var (code, o, e) = Run("export-csv", f, outp, "--sheet", "data");
        Assert.Equal(0, code);
        Assert.Empty(o);
        Assert.Empty(e);
        Assert.Equal(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("hi,\r\n,42\r\n")).ToArray(), File.ReadAllBytes(outp));

        string quoted = P("my.csv");
        Assert.Equal(0, Run("export-csv", f, quoted, "--delimiter", ";", "--sheet", "My Sheet").code);
        Assert.Equal(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(";;\r\n;;\r\n;;5\r\n")).ToArray(), File.ReadAllBytes(quoted));
    }

    [Fact]
    public void ExportCsv_WithoutSheet_IsTheActiveSheet()
    {
        string f = MultiSheetFile();
        string outp = P("active.csv");
        Assert.Equal(0, Run("export-csv", f, outp).code);
        Assert.Equal(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("21\r\n")).ToArray(), File.ReadAllBytes(outp));
    }

    [Fact]
    public void ExportCsv_UnknownSheet_Exit1_NothingWritten()
    {
        string f = MultiSheetFile();
        string outp = P("x.csv");
        var (code, o, e) = Run("export-csv", f, outp, "--sheet", "Nope");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(o);
        AssertSingleErrorLine(e);
        Assert.Contains("unknown sheet 'Nope'", e);
        Assert.False(File.Exists(outp));
    }

    // ---- script: sheet commands ----

    [Fact]
    public void Sheets_NewWorkbook_ByteExact()
    {
        var (code, o) = Script("SHEETS\n");
        Assert.Equal(0, code);
        Assert.Equal("SHEET\t0\tSheet1\nACTIVE\tSheet1\n", o);
    }

    [Fact]
    public void AddRenameMoveActivateDelete_QuotedNames_ByteExact()
    {
        string script = Lines(
            "ADDSHEET Data",
            "ADDSHEET 'My Sheet' 0",
            "ADDSHEET 'O''Brien'",
            "ADDSHEET '2024' 1",
            "SHEETS",
            "ACTIVATE 'my sheet'",
            "RENAMESHEET 'My Sheet' 'Your Sheet'",
            "MOVESHEET 'o''brien' 0",
            "RENAMESHEET data DATA",
            "SHEETS",
            "DELETESHEET '2024'",
            "DELETESHEET 'Your Sheet'",
            "SHEETS");
        var (code, o) = Script(script);
        Assert.Equal(0, code);
        Assert.Equal(Lines(
            "SHEET\t0\tMy Sheet", "SHEET\t1\t2024", "SHEET\t2\tSheet1", "SHEET\t3\tData", "SHEET\t4\tO'Brien", "ACTIVE\tSheet1",
            "SHEET\t0\tO'Brien", "SHEET\t1\tYour Sheet", "SHEET\t2\t2024", "SHEET\t3\tSheet1", "SHEET\t4\tDATA", "ACTIVE\tYour Sheet",
            // R80: deleting the active sheet at index 1 activates the sheet now at index 1
            "SHEET\t0\tO'Brien", "SHEET\t1\tSheet1", "SHEET\t2\tDATA", "ACTIVE\tSheet1"), o);
    }

    [Fact]
    public void CommandWord_IsCaseInsensitive_AndNamesKeepTheirCasing()
    {
        var (code, o) = Script(Lines("addsheet mixedCase", "activate MIXEDCASE", "sheets"));
        Assert.Equal(0, code);
        Assert.Equal(Lines("SHEET\t0\tSheet1", "SHEET\t1\tmixedCase", "ACTIVE\tmixedCase"), o);
    }

    [Fact]
    public void RenameSheet_RewritesFormulas_AndNamesPrintQualified()
    {
        string script = Lines(
            "ADDSHEET Data",
            "SET Data!A1 7",
            "SET B1 =Data!A1*2",
            "NAME rate 'Data'!$A$1",
            "NAME here $B$1",
            "RENAMESHEET Data 'My Data'",
            "INPUT B1",
            "GET B1",
            "NAMES");
        var (code, o) = Script(script);
        Assert.Equal(0, code);
        Assert.Equal(Lines(
            "B1\t='My Data'!A1*2",
            "B1\tN:14",
            "NAME\there\tSheet1!$B$1",   // R77: an unqualified refersTo is qualified with the active sheet
            "NAME\trate\t'My Data'!$A$1"), o);
    }

    [Fact]
    public void Name_QualifiedRefersToWithSpaces()
    {
        var (code, o) = Script(Lines("ADDSHEET 'My Sheet'", "SET 'My Sheet'!A1 3", "NAME x 'My Sheet'!$A$1", "SET A2 =x+1", "NAMES", "GET A2"));
        Assert.Equal(0, code);
        Assert.Equal(Lines("NAME\tx\t'My Sheet'!$A$1", "A2\tN:4"), o);
    }

    [Fact]
    public void InsertCols_QuotedSheet_ExampleFromTheSpec()
    {
        string script = Lines(
            "ADDSHEET 'My Data'",
            "SET 'My Data'!C1 5",
            "SET 'My Data'!D1 ='My Data'!C1+1",
            "SET A1 ='My Data'!D1",
            "SET C1 unmoved",
            "INSERTCOLS 'My Data'!C 2",
            "INPUT 'My Data'!E1",
            "INPUT 'My Data'!F1",
            "INPUT A1",
            "INPUT C1",
            "DUMP");
        var (code, o) = Script(script);
        Assert.Equal(0, code);
        Assert.Equal(Lines(
            "'My Data'!E1\t5",
            "'My Data'!F1\t='My Data'!E1+1",
            "A1\t='My Data'!F1",
            "C1\tunmoved",
            "Sheet1!A1\tN:6",
            "Sheet1!C1\tT:unmoved",
            "'My Data'!E1\tN:5",
            "'My Data'!F1\tN:6"), o);
    }

    [Fact]
    public void RowAndColumnCommands_PerSheet_AndUnqualifiedActOnActiveSheet()
    {
        string script = Lines(
            "ADDSHEET Data",
            "SET Data!A2 x",
            "SET A2 y",
            "INSERTROWS data!1 1",      // lowercase sheet name
            "INPUT Data!A3",
            "INPUT A2",
            "DELETEROWS Data!1 1",
            "INPUT Data!A2",
            "ACTIVATE Data",
            "INSERTCOLS a 1",           // active sheet: Data
            "INPUT B2",
            "INPUT Sheet1!A2",
            "DELETECOLS 'Data'!A 1",
            "INPUT A2",
            "UNDO",
            "INPUT B2");
        var (code, o) = Script(script);
        Assert.Equal(0, code);
        Assert.Equal(Lines(
            "Data!A3\tx", "A2\ty", "Data!A2\tx", "B2\tx", "Sheet1!A2\ty", "A2\tx", "OK", "B2\tx"), o);
    }

    [Fact]
    public void SheetCommands_AreOneUndoStepEach_AndActivateIsNot()
    {
        var (code, o) = Script(Lines("ADDSHEET Data", "ACTIVATE Data", "DIRTY", "UNDO", "SHEETS", "UNDO", "REDO", "SHEETS"));
        Assert.Equal(0, code);
        Assert.Equal(Lines(
            "TRUE", "OK",
            "SHEET\t0\tSheet1", "ACTIVE\tSheet1",   // R80: undo of AddSheet moves activation
            "NOOP", "OK",
            "SHEET\t0\tSheet1", "SHEET\t1\tData", "ACTIVE\tSheet1"), o);
    }

    [Fact]
    public void Activate_LeavesDirtyAndHistoryAlone()
    {
        string f = P("clean.gridline");
        var (code, o) = Script(Lines("ADDSHEET Data", "SAVE " + f, "ACTIVATE Data", "DIRTY", "UNDO", "SHEETS"));
        Assert.Equal(0, code);
        // R80: ACTIVATE is no undo step and keeps the workbook clean, so UNDO undoes ADDSHEET; the active Data is removed and
        // the last sheet, Sheet1, becomes active.
        Assert.Equal(Lines("FALSE", "OK", "SHEET\t0\tSheet1", "ACTIVE\tSheet1"), o);
    }

    // ---- script: qualified addresses in SET/SETX/GET/INPUT/PASTE ----

    [Fact]
    public void SetGetInput_AcrossSheets_EchoAsGiven()
    {
        string script = Lines(
            "ADDSHEET Data",
            "ADDSHEET 'My Sheet'",
            "SET 'my sheet'!a1 hello  world ",   // the input is the rest of the line, verbatim
            "SETX data!b1 a\\tb",
            "SET A1 =Data!B1&'My Sheet'!A1",
            "SET 'O''x",                          // unterminated quote → ERR
            "GET 'MY SHEET'!A1",
            "INPUT 'My Sheet'!$A$1",
            "INPUT DATA!B1",
            "GET a1",
            "GET 'Sheet1'!A1",
            "SET 'My Sheet'!A1",                  // no input clears
            "GET 'My Sheet'!A1",
            "DUMP");
        var (code, o) = Script(script);
        string blank = new Workbook().GetValue("A1").ToCanonicalString();
        Assert.Equal(4, code);
        Assert.Equal(Lines(
            "ERR\tunterminated quoted sheet name",
            "'My Sheet'!A1\tT:hello  world ",
            "'My Sheet'!A1\thello  world ",
            "Data!B1\ta\\tb",
            "A1\tT:a\\tbhello  world ",          // canonical values escape the tab
            "Sheet1!A1\tT:a\\tbhello  world ",
            "'My Sheet'!A1\t" + blank,
            "Sheet1!A1\tT:a\\tb",
            "Data!B1\tT:a\\tb"), o);
    }

    [Fact]
    public void Dump_SingleSheet_Unqualified_MultiSheet_Qualified()
    {
        var (code, o) = Script(Lines("SET B1 2", "SET A1 1", "DUMP", "ADDSHEET 'My Sheet' 0", "SET 'My Sheet'!A1 3", "DUMP", "DELETESHEET 'My Sheet'", "DUMP"));
        Assert.Equal(0, code);
        Assert.Equal(Lines(
            "A1\tN:1", "B1\tN:2",
            "'My Sheet'!A1\tN:3", "Sheet1!A1\tN:1", "Sheet1!B1\tN:2",
            "A1\tN:1", "B1\tN:2"), o);
    }

    [Fact]
    public void Paste_AcrossSheets()
    {
        string script = Lines(
            "ADDSHEET 'My Sheet'",
            "SET A1 1",
            "SET B1 =A1+1",
            "PASTE A1:B1 'my sheet'!C5",
            "INPUT 'My Sheet'!D5",
            "GET 'My Sheet'!D5",
            "PASTE 'My Sheet'!C5:D5 A3",
            "INPUT B3",
            "PASTE Nope!A1 A1",
            "PASTE A1 Nope!A1");
        var (code, o) = Script(script);
        Assert.Equal(4, code);
        string[] lines = o.Split('\n');
        Assert.Equal("'My Sheet'!D5\t=C5+1", lines[0]);
        Assert.Equal("'My Sheet'!D5\tN:2", lines[1]);
        Assert.Equal("B3\t=A3+1", lines[2]);
        Assert.StartsWith("ERR\t", lines[3]);
        Assert.StartsWith("ERR\t", lines[4]);
        Assert.Equal(6, lines.Length);
    }

    [Fact]
    public void BeginEnd_QualifiedSetsStillBatch_SheetCommandsAreErr()
    {
        string script = Lines(
            "ADDSHEET 'My Sheet'",
            "BEGIN",
            "SET 'My Sheet'!A1 1",
            "SETX 'my sheet'!B1 =A1+1",
            "ADDSHEET X",
            "RENAMESHEET 'My Sheet' Y",
            "DELETESHEET 'My Sheet'",
            "MOVESHEET 'My Sheet' 0",
            "ACTIVATE 'My Sheet'",
            "SHEETS",
            "INSERTCOLS 'My Sheet'!A 1",
            "SET Nope!A1 1",
            "END",
            "SHEETS",
            "GET 'My Sheet'!B1",
            "UNDO",
            "GET 'My Sheet'!A1");
        var (code, o) = Script(script);
        string blank = new Workbook().GetValue("A1").ToCanonicalString();
        Assert.Equal(4, code);
        const string NotAllowed = "ERR\tcommand not allowed inside BEGIN block";
        Assert.Equal(Lines(
            NotAllowed, NotAllowed, NotAllowed, NotAllowed, NotAllowed, NotAllowed, NotAllowed,
            "ERR\tunknown sheet 'Nope'",
            "SHEET\t0\tSheet1", "SHEET\t1\tMy Sheet", "ACTIVE\tSheet1",
            "'My Sheet'!B1\tN:2",
            "OK",                                  // the batch is one undo step
            "'My Sheet'!A1\t" + blank), o);
    }

    // ---- script: ERR cases ----

    [Theory]
    [InlineData("ADDSHEET X -1", "ERR\tinvalid index '-1' (expected a non-negative integer)")]
    [InlineData("ADDSHEET X +1", "ERR\tinvalid index '+1' (expected a non-negative integer)")]
    [InlineData("ADDSHEET X 1.0", "ERR\tinvalid index '1.0' (expected a non-negative integer)")]
    [InlineData("ADDSHEET X 99999999999", "ERR\tindex '99999999999' is too large")]
    [InlineData("MOVESHEET Sheet1 x", "ERR\tinvalid index 'x' (expected a non-negative integer)")]
    [InlineData("ADDSHEET", "ERR\tADDSHEET requires <name> [index]")]
    [InlineData("ADDSHEET X 1 2", "ERR\tADDSHEET requires <name> [index]")]
    [InlineData("ADDSHEET  X", "ERR\tADDSHEET requires <name> [index]")]
    [InlineData("ADDSHEET X ", "ERR\tADDSHEET requires <name> [index]")]
    [InlineData("ADDSHEET My Sheet", "ERR\tinvalid index 'Sheet' (expected a non-negative integer)")]
    [InlineData("ADDSHEET 2024", "ERR\tsheet name '2024' must be quoted")]
    [InlineData("ADDSHEET O'Brien", "ERR\tsheet name 'O'Brien' must be quoted")]
    [InlineData("ADDSHEET a:b", "ERR\tinvalid sheet name 'a:b'")]
    [InlineData("ADDSHEET 'My Sheet", "ERR\tunterminated quoted sheet name")]
    [InlineData("ADDSHEET 'a'b", "ERR\tinvalid quoted sheet name 'a'b")]
    [InlineData("RENAMESHEET Sheet1", "ERR\tRENAMESHEET requires <old> <new>")]
    [InlineData("DELETESHEET", "ERR\tDELETESHEET requires <name>")]
    [InlineData("MOVESHEET Sheet1", "ERR\tMOVESHEET requires <name> <index>")]
    [InlineData("ACTIVATE", "ERR\tACTIVATE requires <name>")]
    [InlineData("ACTIVATE A B", "ERR\tACTIVATE requires <name>")]
    [InlineData("SHEETS x", "ERR\tSHEETS takes no arguments")]
    [InlineData("INSERTROWS Sheet1! 2", "ERR\tmissing row")]
    [InlineData("INSERTROWS My Sheet!1 2", "ERR\tINSERTROWS requires [<sheet>!]<row> <n>")]
    [InlineData("INSERTCOLS 2024!C 1", "ERR\tinvalid sheet prefix in '2024!C'")]
    [InlineData("INSERTCOLS Sheet1!1 1", "ERR\tinvalid column '1' (expected A to ZZ)")]
    [InlineData("GET Sheet1!A1 B1", "ERR\ttoo many arguments")]
    [InlineData("GET Nope!A1", "ERR\tunknown sheet 'Nope'")]
    [InlineData("INPUT nope!A1", "ERR\tunknown sheet 'nope'")]
    [InlineData("SET Nope!A1 1", "ERR\tunknown sheet 'Nope'")]
    [InlineData("SET My Sheet!A1 1", "ERR\tinvalid cell address 'My'")]
    [InlineData("GET 'Sheet1'A1", "ERR\tinvalid cell address ''Sheet1'A1'")]
    public void CliSideErrors_ExactMessages(string line, string expected)
    {
        var (code, o) = Script(line + "\nSHEETS\n");
        Assert.Equal(4, code);
        Assert.Equal(expected + "\nSHEET\t0\tSheet1\nACTIVE\tSheet1\n", o); // and the workbook is unchanged
    }

    [Theory]
    [InlineData("ADDSHEET X 2")]                 // index past SheetNames.Count
    [InlineData("ADDSHEET sheet1")]              // duplicate, case-insensitively
    [InlineData("ADDSHEET ''")]                  // empty name
    [InlineData("ADDSHEET 'a:b'")]               // quoted, invalid
    [InlineData("ADDSHEET 'abcdefghijabcdefghijabcdefghijab'")] // 32 characters
    [InlineData("MOVESHEET Sheet1 1")]           // newIndex must be < SheetNames.Count
    [InlineData("MOVESHEET Nope 0")]
    [InlineData("RENAMESHEET Nope X")]
    [InlineData("RENAMESHEET Sheet1 'a/b'")]
    [InlineData("DELETESHEET Nope")]
    [InlineData("DELETESHEET Sheet1")]           // the last sheet
    [InlineData("ACTIVATE Nope")]
    [InlineData("INSERTROWS Nope!1 1")]
    [InlineData("DELETECOLS Nope!A 1")]
    [InlineData("PASTE A1 'Nope'!B1")]
    [InlineData("NAME x Nope!$A$1")]
    public void ApiErrors_AreErrLines_WorkbookUnchanged(string line)
    {
        var (code, o) = Script(line + "\nSHEETS\nDIRTY\n");
        Assert.Equal(4, code);
        string[] lines = o.Split('\n');
        Assert.StartsWith("ERR\t", lines[0]);
        Assert.Equal("SHEET\t0\tSheet1\nACTIVE\tSheet1\nFALSE\n", string.Join("\n", lines[1..]));
    }

    [Fact]
    public void AddSheet_256th_IsErr()
    {
        var sb = new StringBuilder();
        for (int i = 2; i <= 256; i++) sb.Append("ADDSHEET Sheet").Append(i).Append('\n');
        var (code, o) = Script(sb.ToString());
        Assert.Equal(4, code);
        Assert.StartsWith("ERR\t", o); // only the 256th fails (R78: 255 sheets at most)
        Assert.Equal(1, o.Count(c => c == '\n'));
    }

    [Fact]
    public void LoadedMultiSheetFile_ScriptSeesItsSheets()
    {
        string f = MultiSheetFile();
        var (code, o) = Script(Lines("SHEETS", "GET Data!B2", "DUMP"), "--file", f);
        Assert.Equal(0, code);
        Assert.Equal(Lines(
            "SHEET\t0\tSheet1", "SHEET\t1\tData", "SHEET\t2\tMy Sheet", "ACTIVE\tSheet1",
            "Data!B2\tN:42",
            "Sheet1!A1\tN:21", "Data!A1\tT:hi", "Data!B2\tN:42", "'My Sheet'!C3\tN:5"), o);
    }

    [Fact]
    public void SaveAndReload_KeepsSheetsAndActiveSheet()
    {
        string f = P("saved.gridline");
        var (code, o) = Script(Lines("ADDSHEET 'My Sheet'", "SET 'My Sheet'!A1 1", "ACTIVATE 'My Sheet'", "SAVE " + f, "LOAD " + f, "SHEETS", "GET A1"));
        Assert.Equal(0, code);
        Assert.Equal(Lines("SHEET\t0\tSheet1", "SHEET\t1\tMy Sheet", "ACTIVE\tMy Sheet", "A1\tN:1"), o);
    }

    // ---- D-58: sheet names with TAB, CR and LF (legal under CR1.1) are escaped on stdout ----

    private const string Ctl = "T\tab\rC\nL";             // as stored
    private const string CtlOut = "T\\tab\\rC\\nL";        // as printed (§8.3 escapes)

    /// <summary>Sheet1 (A1 = 1) and the control-character sheet (A1 = 5, B1 = its A1 + Sheet1!A1), name rate → it.</summary>
    private string ControlCharFile()
    {
        var wb = new Workbook();
        wb.SetCell("A1", "1");
        wb.AddSheet(Ctl);
        string q = Gridline.Core.Addressing.SheetName.Format(Ctl);
        wb.SetCell(q + "!A1", "5");
        wb.SetCell(q + "!B1", "=A1+Sheet1!A1");
        wb.DefineName("rate", q + "!$A$1");
        string path = P("ctl.gridline");
        GridlineFile.SaveToPath(wb, path);
        return path;
    }

    [Fact]
    public void ControlCharSheetName_FromFile_ScriptOutputIsEscaped()
    {
        string f = ControlCharFile();
        var (code, o) = Script(Lines("SHEETS", "DUMP", "NAMES"), "--file", f);
        Assert.Equal(0, code);
        Assert.Equal(Lines(
            "SHEET\t0\tSheet1", "SHEET\t1\t" + CtlOut, "ACTIVE\tSheet1",
            "Sheet1!A1\tN:1", "'" + CtlOut + "'!A1\tN:5", "'" + CtlOut + "'!B1\tN:6",
            "NAME\trate\t'" + CtlOut + "'!$A$1"), o);
    }

    [Fact]
    public void ControlCharSheetName_ActiveFromFile_IsEscaped()
    {
        var wb = new Workbook();
        wb.AddSheet(Ctl);
        wb.ActiveSheet = Ctl;
        string f = P("active-ctl.gridline");
        GridlineFile.SaveToPath(wb, f);
        var (code, o) = Script("SHEETS\n", "--file", f);
        Assert.Equal(0, code);
        Assert.Equal(Lines("SHEET\t0\tSheet1", "SHEET\t1\t" + CtlOut, "ACTIVE\t" + CtlOut), o);
    }

    [Fact]
    public void ControlCharSheetName_QuotedScriptToken_TabAndCr()
    {
        // A TAB and a mid-line CR can be typed in a quoted token (only one trailing CR is stripped, R31).
        const string Name = "x\ty\rz";
        const string Out = "x\\ty\\rz";
        string script = Lines(
            "ADDSHEET '" + Name + "'",
            "SET '" + Name + "'!A1 7",
            "NAME n '" + Name + "'!$A$1",
            "SHEETS",
            "ACTIVATE '" + Name + "'",
            "SHEETS",
            "GET '" + Name + "'!a1",
            "INPUT '" + Name + "'!A1",
            "DUMP",
            "NAMES");
        var (code, o) = Script(script);
        Assert.Equal(0, code);
        Assert.Equal(Lines(
            "SHEET\t0\tSheet1", "SHEET\t1\t" + Out, "ACTIVE\tSheet1",
            "SHEET\t0\tSheet1", "SHEET\t1\t" + Out, "ACTIVE\t" + Out,
            "'" + Out + "'!A1\tN:7",
            "'" + Out + "'!A1\t7",
            "'" + Out + "'!A1\tN:7",
            "NAME\tn\t'" + Out + "'!$A$1"), o);
    }

    [Fact]
    public void ControlCharSheetName_Eval_AndEvalAll_AreEscaped()
    {
        string f = ControlCharFile();
        string q = "'" + Ctl + "'";
        var (code, o, e) = Run("eval", f, q + "!a1", q.ToLowerInvariant() + "!B1", "A1");
        Assert.Equal(0, code);
        Assert.Empty(e);
        Assert.Equal(Lines("'" + CtlOut + "'!A1\tN:5", "'" + CtlOut + "'!B1\tN:6", "A1\tN:1"), o);

        var (c2, all, _) = Run("eval", f, "--all");
        Assert.Equal(0, c2);
        Assert.Equal(Lines("Sheet1!A1\tN:1", "'" + CtlOut + "'!A1\tN:5", "'" + CtlOut + "'!B1\tN:6"), all);
    }

    [Fact]
    public void Process_ControlCharSheetName_EvalAndScript_AreEscaped()
    {
        ControlCharFile();
        var ev = Phase2AcceptanceTests.GridlineIn(_dir, null, "eval", "ctl.gridline", "'" + Ctl + "'!A1", "A1");
        Assert.Equal(0, ev.code);
        Assert.Equal("'" + CtlOut + "'!A1\tN:5\nA1\tN:1\n", ev.stdout);

        var all = Phase2AcceptanceTests.GridlineIn(_dir, null, "eval", "ctl.gridline", "--all");
        Assert.Equal("Sheet1!A1\tN:1\n'" + CtlOut + "'!A1\tN:5\n'" + CtlOut + "'!B1\tN:6\n", all.stdout);

        var sc = Phase2AcceptanceTests.GridlineIn(_dir, Encoding.UTF8.GetBytes("SHEETS\nDUMP\nNAMES\n"), "script", "--file", "ctl.gridline");
        Assert.Equal(0, sc.code);
        Assert.Equal(
            "SHEET\t0\tSheet1\nSHEET\t1\t" + CtlOut + "\nACTIVE\tSheet1\n" +
            "Sheet1!A1\tN:1\n'" + CtlOut + "'!A1\tN:5\n'" + CtlOut + "'!B1\tN:6\n" +
            "NAME\trate\t'" + CtlOut + "'!$A$1\n", sc.stdout);
    }

    // ---- process level (the built gridline.dll) ----

    [Fact]
    public void Process_SheetScript_ByteExact()
    {
        string script = Lines(
            "ADDSHEET 'My Data'",
            "ADDSHEET 'It''s' 0",
            "SET 'My Data'!C1 5",
            "SET A1 ='My Data'!C1*2",
            "INSERTCOLS 'My Data'!C 2",
            "INPUT a1",
            "GET 'my data'!e1",
            "SHEETS",
            "DUMP",
            "DELETESHEET Nope");
        var (code, stdout, stderr, bytes) = Phase2AcceptanceTests.GridlineIn(_dir, new UTF8Encoding(false).GetBytes(script), "script");
        Assert.Equal(4, code);
        Assert.Empty(stderr);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF, "no BOM");
        string[] lines = stdout.Split('\n');
        Assert.Equal(Lines(
            "A1\t='My Data'!E1*2",
            "'My Data'!E1\tN:5",
            "SHEET\t0\tIt's", "SHEET\t1\tSheet1", "SHEET\t2\tMy Data", "ACTIVE\tSheet1",
            "Sheet1!A1\tN:10",
            "'My Data'!E1\tN:5"), string.Join("\n", lines[..^2]) + "\n");
        Assert.StartsWith("ERR\t", lines[^2]);
        Assert.Equal("", lines[^1]);
    }

    [Fact]
    public void Process_ImportCsvSheet_EvalQualified_ExportCsvSheet()
    {
        File.WriteAllBytes(Path.Combine(_dir, "in.csv"), Encoding.UTF8.GetBytes("1,2\n3,4\n"));
        var imp = Phase2AcceptanceTests.GridlineIn(_dir, null, "import-csv", "in.csv", "out.gridline", "--sheet", "Data");
        Assert.True(imp.code == 0, imp.stderr);

        var ev = Phase2AcceptanceTests.GridlineIn(_dir, null, "eval", "out.gridline", "Data!A1", "data!$b$2", "A2");
        Assert.True(ev.code == 0, ev.stderr);
        Assert.Equal("Data!A1\tN:1\nData!B2\tN:4\nA2\tN:3\n", ev.stdout);

        var all = Phase2AcceptanceTests.GridlineIn(_dir, null, "eval", "out.gridline", "--all");
        Assert.Equal("A1\tN:1\nB1\tN:2\nA2\tN:3\nB2\tN:4\n", all.stdout); // one sheet: unqualified

        var exp = Phase2AcceptanceTests.GridlineIn(_dir, null, "export-csv", "out.gridline", "out.csv", "--sheet", "Data");
        Assert.True(exp.code == 0, exp.stderr);
        Assert.Equal(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("1,2\r\n3,4\r\n")).ToArray(),
            File.ReadAllBytes(Path.Combine(_dir, "out.csv")));

        var bad = Phase2AcceptanceTests.GridlineIn(_dir, null, "export-csv", "out.gridline", "bad.csv", "--sheet", "Sheet1");
        Assert.Equal(1, bad.code);
        Assert.False(File.Exists(Path.Combine(_dir, "bad.csv")));

        var unknown = Phase2AcceptanceTests.GridlineIn(_dir, null, "eval", "out.gridline", "Sheet1!A1");
        Assert.Equal(5, unknown.code);
        Assert.Empty(unknown.stdout);
    }

    [Fact]
    public void Process_EvalAll_MultiSheet()
    {
        MultiSheetFile("m.gridline");
        var r = Phase2AcceptanceTests.GridlineIn(_dir, null, "eval", "m.gridline", "--all");
        Assert.Equal(0, r.code);
        Assert.Equal("Sheet1!A1\tN:21\nData!A1\tT:hi\nData!B2\tN:42\n'My Sheet'!C3\tN:5\n", r.stdout);
        var q = Phase2AcceptanceTests.GridlineIn(_dir, null, "eval", "m.gridline", "'my sheet'!c3", "c3");
        Assert.Equal("'My Sheet'!C3\tN:5\nC3\t" + new Workbook().GetValue("A1").ToCanonicalString() + "\n", q.stdout);
    }
}
