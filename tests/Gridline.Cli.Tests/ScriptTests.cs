using System.Text;
using Gridline.Core;
using Gridline.IO;

namespace Gridline.Cli.Tests;

public sealed class ScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridline-script-" + Guid.NewGuid().ToString("N"));

    public ScriptTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static (int code, string stdout, string stderr) Script(string script, params string[] extra)
    {
        var o = new MemoryStream();
        var e = new MemoryStream();
        string[] args = new[] { "script" }.Concat(extra).ToArray();
        int code = CliApp.Run(args, new StringReader(script), o, e);
        return (code, Encoding.UTF8.GetString(o.ToArray()), Encoding.UTF8.GetString(e.ToArray()));
    }

    [Fact]
    public void Stdin_BomPrefixedScript_FirstCommandWorks()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("SET A1 café\nSET B1 =LEN(A1)\nGET B1\n")];
        var o = new MemoryStream();
        int code = CliApp.Run(["script"], CliApp.OpenStdin(new MemoryStream(bytes)), o, new MemoryStream());
        Assert.Equal(0, code);
        Assert.Equal("B1\tN:4\n", Encoding.UTF8.GetString(o.ToArray()));
    }

    [Fact]
    public void SetKeepsSpacesInInput_AndGetPrintsCanonical()
    {
        var (code, o, _) = Script("SET A1 =1 + 2\nGET A1\nSET B1 hello world\nGET b1\n");
        Assert.Equal(0, code);
        Assert.Equal("A1\tN:3\nB1\tT:hello world\n", o);
    }

    [Fact]
    public void SetWithNoInputOrTrailingSpaceClears()
    {
        var (code, o, _) = Script("SET A1 5\nSET A1\nGET A1\nSET A1 5\nSET A1 \nGET A1\n");
        Assert.Equal(0, code);
        string blank = new Workbook().GetValue("A1").ToCanonicalString();
        Assert.Equal($"A1\t{blank}\nA1\t{blank}\n", o);
    }

    [Fact]
    public void SetxUnescapes()
    {
        var (code, o, _) = Script("SETX A1 a\\tb\\\\c\\nd\\re\nINPUT A1\nGET A1\n");
        Assert.Equal(0, code);
        Assert.Equal("A1\ta\\tb\\\\c\\nd\\re\nA1\tT:a\\tb\\\\c\\nd\\re\n", o);
    }

    [Theory]
    [InlineData("SETX A1 a\\qb")]
    [InlineData("SETX A1 abc\\")]
    public void SetxBadEscapePrintsErr(string line)
    {
        var (code, o, _) = Script(line + "\nGET A1\n");
        Assert.Equal(4, code);
        Assert.StartsWith("ERR\t", o);
        Assert.Equal(2, o.Count(c => c == '\n'));
        Assert.DoesNotContain("T:", o);
    }

    [Fact]
    public void InputShowsRawInputEscaped()
    {
        var (_, o, _) = Script("SET A1 =1+2\nINPUT A1\nINPUT B7\n");
        Assert.Equal("A1\t=1+2\nB7\t\n", o);
    }

    [Fact]
    public void DumpIsRowMajor()
    {
        var (code, o, _) = Script("SET B1 2\nSET A2 3\nSET A1 1\nDUMP\n");
        Assert.Equal(0, code);
        Assert.Equal("A1\tN:1\nB1\tN:2\nA2\tN:3\n", o);
    }

    [Fact]
    public void StatsBeforeAnyEditIsZero_AndAfterChainEditCountsFormulas()
    {
        var (_, o, _) = Script("STATS\nSET A1 1\nSET A2 =A1+1\nSET A3 =A2+1\nSET A1 5\nSTATS\nSET A1 5\nSTATS\n");
        Assert.Equal("EVALUATED\t0\nEVALUATED\t2\nEVALUATED\t0\n", o);
    }

    [Fact]
    public void ErrContinuesAndExitIs4()
    {
        var (code, o, e) = Script("SET ZZZZ9999999 1\nSET A1 7\nGET A1\nGET\nGET A1 B1\nDUMP x\n");
        Assert.Equal(4, code);
        var lines = o.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.StartsWith("ERR\t", lines[0]);
        Assert.Equal("A1\tN:7", lines[1]);
        Assert.StartsWith("ERR\t", lines[2]);
        Assert.StartsWith("ERR\t", lines[3]);
        Assert.StartsWith("ERR\t", lines[4]);
        Assert.Empty(e);
    }

    [Fact]
    public void OversizedInputPrintsErr()
    {
        var (code, o, _) = Script("SET A1 " + new string('x', Workbook.MaxInputLength + 1) + "\nGET A1\n");
        Assert.Equal(4, code);
        Assert.StartsWith("ERR\t", o);
        Assert.Contains("A1\tBLANK", o);
    }

    [Fact]
    public void CrlfScriptsWork_AndOnlyOneCrIsStripped()
    {
        var (code, o, _) = Script("SET A1 hi\r\nGET A1\r\n\r\n# c\r\nSET B1 x\r\r\nINPUT B1\r\n");
        Assert.Equal(0, code);
        Assert.Equal("A1\tT:hi\nB1\tx\\r\n", o);
    }

    [Fact]
    public void CommentsAndBlankLinesIgnored_CaseInsensitiveCommands()
    {
        var (code, o, _) = Script("# comment\n\n   \nset a1 4\nGeT A1\n");
        Assert.Equal(0, code);
        Assert.Equal("A1\tN:4\n", o);
    }

    [Fact]
    public void UnknownCommandsPrintNotImplemented()
    {
        var (code, o, _) = Script("NAMESX\nFROBNICATE 1 2\n");
        Assert.Equal(4, code);
        Assert.Equal("ERR\tnot implemented\nERR\tnot implemented\n", o);
    }

    [Fact]
    public void SaveThenLoadRoundTrips_PathWithSpaces()
    {
        string path = Path.Combine(_dir, "my book.gridline");
        var (c1, _, _) = Script($"SET A1 =2*3\nSAVE {path}\n");
        Assert.Equal(0, c1);
        var (c2, o, _) = Script($"LOAD {path}\nGET A1\nINPUT A1\n");
        Assert.Equal(0, c2);
        Assert.Equal("A1\tN:6\nA1\t=2*3\n", o);
    }

    [Fact]
    public void LoadFailureKeepsWorkbook()
    {
        string bad = Path.Combine(_dir, "bad.gridline");
        File.WriteAllText(bad, "not json");
        string missing = Path.Combine(_dir, "missing.gridline");
        var (code, o, _) = Script($"SET A1 9\nLOAD {missing}\nLOAD {bad}\nLOAD\nGET A1\n");
        Assert.Equal(4, code);
        var lines = o.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.All(lines.Take(3), l => Assert.StartsWith("ERR\t", l));
        Assert.Equal("A1\tN:9", lines[3]);
    }

    [Fact]
    public void SaveFailurePrintsErr()
    {
        string path = Path.Combine(_dir, "nodir", "x.gridline");
        var (code, o, _) = Script($"SAVE {path}\n");
        Assert.Equal(4, code);
        Assert.StartsWith("ERR\t", o);
    }

    [Fact]
    public void FilePreloadsWorkbook()
    {
        string path = Path.Combine(_dir, "pre.gridline");
        GridlineFile.SaveToPath(Workbook.FromCells(new[] { new KeyValuePair<string, string>("A1", "41") }), path);
        var (code, o, _) = Script("SET B1 =A1+1\nGET B1\n", "--file", path);
        Assert.Equal(0, code);
        Assert.Equal("B1\tN:42\n", o);
    }

    [Fact]
    public void FileMissingExits2WithoutReadingStdin()
    {
        var o = new MemoryStream();
        var e = new MemoryStream();
        int code = CliApp.Run(new[] { "script", "--file", Path.Combine(_dir, "nope.gridline") }, new ThrowingReader(), o, e);
        Assert.Equal(2, code);
        Assert.Empty(o.ToArray());
        Assert.StartsWith("error: ", Encoding.UTF8.GetString(e.ToArray()));
    }

    [Fact]
    public void FileFormatErrorExits3()
    {
        string bad = Path.Combine(_dir, "bad.gridline");
        File.WriteAllText(bad, "{}");
        var (code, o, e) = Script("GET A1\n", "--file", bad);
        Assert.Equal(3, code);
        Assert.Empty(o);
        Assert.StartsWith("error: ", e);
    }

    [Theory]
    [InlineData("--file")]
    [InlineData("--x")]
    [InlineData("a.gridline")]
    public void BadUsageExits1(string arg)
    {
        var (code, o, _) = Script("GET A1\n", arg);
        Assert.Equal(1, code);
        Assert.Empty(o);
    }

    [Fact]
    public void ExtraArgsExit1()
    {
        var (code, _, _) = Script("", "--file", "a", "b");
        Assert.Equal(1, code);
    }

    [Fact]
    public void OutputHasNoBomAndLfOnly()
    {
        var o = new MemoryStream();
        CliApp.Run(new[] { "script" }, new StringReader("SET A1 1\r\nGET A1\r\n"), o, new MemoryStream());
        byte[] b = o.ToArray();
        Assert.NotEqual(0xEF, b[0]);
        Assert.DoesNotContain((byte)'\r', b);
    }

    private sealed class FailAfterStream(int okWrites) : Stream
    {
        private int _writes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (++_writes > okWrites) throw new IOException("broken pipe");
        }
    }

    // §10.1, §10.3: a flush failure mid-script never escapes Run; exit 4 if an ERR line was already produced, else 1.
    [Fact]
    public void BrokenPipeMidScript_AfterErrLine_Exits4()
    {
        var e = new MemoryStream();
        int code = CliApp.Run(["script"], new StringReader("GET\nSET A1 1\nGET A1\n"), new FailAfterStream(1), e);
        Assert.Equal(4, code);
        string err = Encoding.UTF8.GetString(e.ToArray());
        Assert.Equal("error: cannot write output: broken pipe\n", err);
    }

    [Fact]
    public void BrokenPipeMidScript_WithoutErrLine_Exits1WithOutputMessage()
    {
        var e = new MemoryStream();
        int code = CliApp.Run(["script"], new StringReader("SET A1 1\nGET A1\nGET A1\n"), new FailAfterStream(1), e);
        Assert.Equal(1, code);
        string err = Encoding.UTF8.GetString(e.ToArray());
        Assert.Equal("error: cannot write output: broken pipe\n", err);
    }

    // R31: exit 4 only if an ERR line was delivered; here the failing flush is the one carrying the first ERR.
    [Fact]
    public void BrokenPipe_OnFirstErrLineFlush_Exits1()
    {
        var e = new MemoryStream();
        int code = CliApp.Run(["script"], new StringReader("GET\n"), new FailAfterStream(0), e);
        Assert.Equal(1, code);
        Assert.Equal("error: cannot write output: broken pipe\n", Encoding.UTF8.GetString(e.ToArray()));
    }

    // §10.3: a line longer than 2×MaxInputLength + 64 (R33) is discarded up to LF, reported as ERR, and the script continues.
    [Fact]
    public void OverlongLine_IsDiscardedWithErr_AndScriptContinues()
    {
        string longLine = "SET A1 " + new string('x', ScriptCommand.MaxLineLength);
        var (code, o, e) = Script(longLine + "\nSET B1 2\nGET B1\n");
        Assert.Equal(4, code);
        Assert.Equal("ERR\tline too long (limit " + ScriptCommand.MaxLineLength + " characters)\nB1\tN:2\n", o);
        Assert.Empty(e);
    }

    [Fact]
    public void OverlongUnterminatedFinalLine_IsErr()
    {
        var (code, o, _) = Script("SET B1 2\n" + new string('y', ScriptCommand.MaxLineLength + 10));
        Assert.Equal(4, code);
        Assert.StartsWith("ERR\tline too long", o);
    }

    [Fact]
    public void LineAtExactLimit_IsNotOverflow()
    {
        string line = "#" + new string('z', ScriptCommand.MaxLineLength - 1);
        var (code, o, _) = Script(line + "\nSET A1 1\nGET A1\n");
        Assert.Equal(0, code);
        Assert.Equal("A1\tN:1\n", o);
    }

    // R33: cap is 2 x MaxInputLength + 64, not counting one trailing CR.
    [Fact]
    public void Setx_With20000Tabs_IsAcceptedAndStored()
    {
        string escaped = string.Concat(Enumerable.Repeat(((char)92) + "t", 20000));
        var (code, o, _) = Script("SETX A1 " + escaped + "\nGET A1\n");
        Assert.Equal(0, code);
        Assert.Equal("A1\tT:" + string.Concat(Enumerable.Repeat(((char)92) + "t", 20000)) + "\n", o);
    }

    [Fact]
    public void LineOfExactlyMax_Passes_MaxPlusOne_IsErr_AndNextLineRuns()
    {
        string atMax = "#" + new string('z', ScriptCommand.MaxLineLength - 1);
        var (c1, o1, _) = Script(atMax + "\nSET A1 1\nGET A1\n");
        Assert.Equal(0, c1);
        Assert.Equal("A1\tN:1\n", o1);

        var (c2, o2, _) = Script(atMax + "z\nSET A1 1\nGET A1\n");
        Assert.Equal(4, c2);
        Assert.Equal("ERR\tline too long (limit " + ScriptCommand.MaxLineLength + " characters)\nA1\tN:1\n", o2);
    }

    [Fact]
    public void CrlfLineWithContentExactlyMax_Passes()
    {
        string atMax = "#" + new string('z', ScriptCommand.MaxLineLength - 1);
        var (code, o, _) = Script(atMax + "\r\nSET A1 1\r\nGET A1\r\n");
        Assert.Equal(0, code);
        Assert.Equal("A1\tN:1\n", o);
        var (c2, o2, _) = Script(atMax + "z\r\nGET A1\r\n");
        Assert.Equal(4, c2);
        Assert.StartsWith("ERR\tline too long", o2);
    }

    [Fact]
    public void MidLineLoneCr_IsKeptInSetInput()
    {
        var (code, o, _) = Script("SET A1 a\rb\nINPUT A1\n");
        Assert.Equal(0, code);
        Assert.Equal("A1\ta\\rb\n", o);
    }

    [Fact]
    public void UnterminatedFinalLine_IsExecuted()
    {
        var (code, o, _) = Script("SET A1 5\nGET A1");
        Assert.Equal(0, code);
        Assert.Equal("A1\tN:5\n", o);
    }

    [Fact]
    public void EmptyStdin_Exit0NoOutput()
    {
        var (code, o, e) = Script("");
        Assert.Equal(0, code);
        Assert.Empty(o);
        Assert.Empty(e);
    }

    [Fact]
    public void SetxDoubleBackslashT_StoresBackslashThenT()
    {
        var (code, o, _) = Script("SETX A1 \\\\t\nINPUT A1\n");
        Assert.Equal(0, code);
        // stored text is backslash + t (2 chars); INPUT escapes the backslash again
        Assert.Equal("A1\t\\\\t\n", o);
    }

    [Fact]
    public void TrailingCrAtEofWithoutLf_IsStripped()
    {
        var (code, o, _) = Script("SET A1 5\nGET A1\r");
        Assert.Equal(0, code);
        Assert.Equal("A1\tN:5\n", o);
    }

    private sealed class ThrowingReader : TextReader
    {
        public override string? ReadLine() => throw new InvalidOperationException("stdin must not be read");
        public override int Read() => throw new InvalidOperationException("stdin must not be read");
    }
}
