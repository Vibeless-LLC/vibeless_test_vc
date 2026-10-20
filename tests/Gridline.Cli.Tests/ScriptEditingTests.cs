using System.Text;

namespace Gridline.Cli.Tests;

/// <summary>§10.3 Phase 2 script commands and ruling R40, in-process.</summary>
public sealed class ScriptEditingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridline-edit-" + Guid.NewGuid().ToString("N"));

    public ScriptEditingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static (int code, string stdout) Script(string script)
    {
        var o = new MemoryStream();
        int code = CliApp.Run(["script"], new StringReader(script), o, new MemoryStream());
        return (code, Encoding.UTF8.GetString(o.ToArray()));
    }

    private static string Blank() => new Gridline.Core.Workbook().GetValue("A1").ToCanonicalString();

    private static string[] Lines(string o) => o.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void UndoRedo_PrintOkOrNoop_AndCaseInsensitive()
    {
        var (code, o) = Script("undo\nREDO\nSET A1 5\nUndo\nGET A1\nredo\nGET A1\nREDO\n");
        Assert.Equal(0, code);
        Assert.Equal("NOOP\nNOOP\nOK\nA1\t" + Blank() + "\nOK\nA1\tN:5\nNOOP\n", o);
    }

    [Theory]
    [InlineData("UNDO x")]
    [InlineData("REDO x")]
    [InlineData("DIRTY x")]
    [InlineData("BEGIN x")]
    [InlineData("END x")]
    public void NoArgumentCommands_WithArguments_Err(string line)
    {
        var (code, o) = Script(line + "\n");
        Assert.Equal(4, code);
        Assert.StartsWith("ERR\t", o);
    }

    [Fact]
    public void TrailingSpace_IsNoArgument_LikeDumpAndStats()
    {
        // The line is split at the first space, so "UNDO " has an empty argument text, the same as "DUMP ".
        var (code, o) = Script("DUMP \nUNDO \nDIRTY \n");
        Assert.Equal(0, code);
        Assert.Equal("NOOP\nFALSE\n", o);
    }

    [Fact]
    public void Begin_BatchIsOneUndoStep_AndPrintsNothing()
    {
        var (code, o) = Script("begin\nSET A1 1\nSETX B1 a\\tb\nSET A1 2\n# c\n\nEnd\nGET A1\nUNDO\nGET A1\nGET B1\nUNDO\n");
        Assert.Equal(0, code);
        Assert.Equal("A1\tN:2\nOK\nA1\t" + Blank() + "\nB1\t" + Blank() + "\nNOOP\n", o);
    }

    [Fact]
    public void Begin_EmptyBlock_IsNoop()
    {
        var (code, o) = Script("BEGIN\nEND\nUNDO\nDIRTY\n");
        Assert.Equal(0, code);
        Assert.Equal("NOOP\nFALSE\n", o);
    }

    [Fact]
    public void Begin_InvalidSetLines_ErrAndAreLeftOut()
    {
        var (code, o) = Script("BEGIN\nSET A1 1\nSET ZZZ9 1\nSETX B1 a\\q\nSET\nSET C1 " + new string('x', 33000) + "\nSET D1 4\nEND\nDUMP\n");
        Assert.Equal(4, code);
        string[] lines = Lines(o);
        Assert.Equal(6, lines.Length);
        for (int i = 0; i < 4; i++) Assert.StartsWith("ERR\t", lines[i]);
        Assert.Equal("A1\tN:1", lines[4]);
        Assert.Equal("D1\tN:4", lines[5]);
    }

    [Fact]
    public void Begin_NestedBegin_Errs_AndBlockStaysOpen()
    {
        var (code, o) = Script("BEGIN\nSET A1 1\nBEGIN\nSET B1 2\nEND\nDUMP\n");
        Assert.Equal(4, code);
        string[] lines = Lines(o);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("ERR\t", lines[0]);
        Assert.Equal("A1\tN:1", lines[1]);
        Assert.Equal("B1\tN:2", lines[2]);
    }

    [Fact]
    public void End_WithoutBegin_Errs()
    {
        var (code, o) = Script("END\nBEGIN\nEND\nEND\n");
        Assert.Equal(4, code);
        string[] lines = Lines(o);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("ERR\t", l));
    }

    [Theory]
    [InlineData("GET A1")]
    [InlineData("UNDO")]
    [InlineData("DIRTY")]
    [InlineData("PASTE A1 B1")]
    [InlineData("DUMP")]
    [InlineData("NAMES")]
    [InlineData("FOO")]
    public void OtherCommandInBlock_ErrNotExecuted_BlockStaysOpen(string cmd)
    {
        var (code, o) = Script("BEGIN\nSET A1 1\n" + cmd + "\nSET B1 2\nEND\nGET B1\n");
        Assert.Equal(4, code);
        string[] lines = Lines(o);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("ERR\t", lines[0]);
        Assert.Equal("B1\tN:2", lines[1]);
    }

    [Fact]
    public void UnterminatedBlock_ErrAtEof_AndDiscards()
    {
        var o = new MemoryStream();
        int code = CliApp.Run(["script"], new StringReader("SET Z1 9\nBEGIN\nSET A1 1"), o, new MemoryStream());
        Assert.Equal(4, code);
        Assert.Equal("ERR\tunterminated BEGIN block\n", Encoding.UTF8.GetString(o.ToArray()));
    }

    [Fact]
    public void Paste_ShiftsReferences_AndPrintsNothing()
    {
        var (code, o) = Script("SET A1 1\nSET B1 =A1+1\nPASTE A1:B1 A3\nGET B3\nINPUT B3\nUNDO\nGET B3\n");
        Assert.Equal(0, code);
        Assert.Equal("B3\tN:2\nB3\t=A3+1\nOK\nB3\t" + Blank() + "\n", o);
    }

    [Theory]
    [InlineData("PASTE")]
    [InlineData("PASTE A1")]
    [InlineData("PASTE A1 B1 C1")]
    [InlineData("PASTE A1:B  C1")]
    [InlineData("PASTE A1: B1")]
    [InlineData("PASTE A1 ZZZ9")]
    [InlineData("PASTE A1:B2 A100000")]
    public void Paste_BadArguments_Err(string line)
    {
        var (code, o) = Script(line + "\n");
        Assert.Equal(4, code);
        Assert.StartsWith("ERR\t", o);
        Assert.Single(Lines(o));
    }

    [Fact]
    public void Dirty_TracksEditSaveUndo()
    {
        string f = Path.Combine(_dir, "a.gridline");
        var (code, o) = Script($"DIRTY\nSET A1 1\nDIRTY\nSAVE {f}\nDIRTY\nSET A1 2\nDIRTY\nUNDO\nDIRTY\nREDO\nDIRTY\nUNDO\nDIRTY\n");
        Assert.Equal(0, code);
        Assert.Equal("FALSE\nTRUE\nFALSE\nTRUE\nOK\nFALSE\nOK\nTRUE\nOK\nFALSE\n", o);
    }

    [Fact]
    public void FailedSave_LeavesDirtyTrue()
    {
        string bad = Path.Combine(_dir, "nodir", "x.gridline");
        var (code, o) = Script($"SET A1 1\nSAVE {bad}\nDIRTY\n");
        Assert.Equal(4, code);
        string[] lines = Lines(o);
        Assert.StartsWith("ERR\t", lines[0]);
        Assert.Equal("TRUE", lines[1]);
    }

    [Fact]
    public void Load_GivesCleanWorkbookWithEmptyHistory()
    {
        string f = Path.Combine(_dir, "b.gridline");
        var (code, o) = Script($"SET A1 1\nSAVE {f}\nSET A1 2\nLOAD {f}\nDIRTY\nUNDO\nGET A1\n");
        Assert.Equal(0, code);
        Assert.Equal("FALSE\nNOOP\nA1\tN:1\n", o);
    }
}
