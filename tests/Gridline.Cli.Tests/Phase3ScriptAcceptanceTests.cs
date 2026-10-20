using System.Text;

namespace Gridline.Cli.Tests;

/// <summary>Process-level acceptance for the §10.3 editing commands (Phase 3 Task 5): drives the built gridline.dll.</summary>
public sealed class Phase3ScriptAcceptanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridline-p3s-" + Guid.NewGuid().ToString("N"));

    public Phase3ScriptAcceptanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void BuildBatchUndoRedoPasteSaveDirty_ByteExact()
    {
        string f = Path.Combine(_dir, "out.gridline");
        string script = string.Join("\n",
            "SET A1 1",
            "BEGIN",
            "SET B1 =A1+1",
            "SETX C1 x\\ty",
            "SET A1 10",
            "END",
            "GET B1",
            "UNDO",
            "GET B1",
            "GET A1",
            "REDO",
            "GET B1",
            "PASTE A1:B1 A3",
            "INPUT B3",
            "DIRTY",
            "SAVE " + f,
            "DIRTY",
            "UNDO",
            "DIRTY",
            "REDO",
            "DIRTY",
            "");
        var (code, stdout, stderr, _) = Phase2AcceptanceTests.GridlineIn(_dir, new UTF8Encoding(false).GetBytes(script), "script");
        string blank = new Gridline.Core.Workbook().GetValue("A1").ToCanonicalString();

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        Assert.Equal(
            "B1\tN:11\nOK\nB1\t" + blank + "\nA1\tN:1\nOK\nB1\tN:11\nB3\t=A3+1\nTRUE\nFALSE\nOK\nTRUE\nOK\nFALSE\n",
            stdout);
        Assert.True(File.Exists(f));
    }
}
