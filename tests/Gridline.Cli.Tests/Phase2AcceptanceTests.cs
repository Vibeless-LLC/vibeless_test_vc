using System.Diagnostics;
using System.Text;

namespace Gridline.Cli.Tests;

/// <summary>
/// Phase 2 process-level acceptance tests (§8.1 file format, §10 CLI, §13 acceptance). Every case launches the built
/// gridline.dll via "dotnet" and feeds stdin as explicit UTF-8 bytes, so results do not depend on the console code page (R31).
/// </summary>
public sealed class Phase2AcceptanceTests : IDisposable
{
    private const string Unicode = "café € \U0001F600 日本";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridline-p2-" + Guid.NewGuid().ToString("N"));

    public Phase2AcceptanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    // §8.1, §10.1: SaveToPath with a bare file name (no directory) resolves against the child's working directory.
    // The test host's own current directory is never changed.
    [Fact]
    public void Set_BareOutFileName_LandsInChildWorkingDirectory()
    {
        string work = P("bare-wd");
        Directory.CreateDirectory(work);
        var (saveCode, _, saveErr, _) = GridlineIn(work, Encoding.UTF8.GetBytes("SET A1 5\nSAVE bare.gridline\n"), "script");
        Assert.True(saveCode == 0, saveErr);
        string saved = Path.Combine(work, "bare.gridline");
        Assert.True(File.Exists(saved));
        Assert.Contains("[\"A1\", \"5\"]", File.ReadAllText(saved));
        Assert.Empty(Directory.GetFiles(work, "*.tmp*"));

        var (setCode, setOut, setErr, _) = GridlineIn(work, null, "set", "bare.gridline", "B1", "7", "--out", "bare2.gridline");
        Assert.True(setCode == 0, setErr);
        Assert.Empty(setOut);
        Assert.True(File.Exists(Path.Combine(work, "bare2.gridline")));
        Assert.Empty(Directory.GetFiles(work, "*.tmp*"));
    }

    private static (int code, string stdout, string stderr, byte[] stdoutBytes) Gridline(byte[]? stdin, params string[] args) =>
        GridlineIn(Path.GetTempPath(), stdin, args); // never depend on the current directory

    internal static (int code, string stdout, string stderr, byte[] stdoutBytes) GridlineIn(string workingDirectory, byte[]? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        psi.ArgumentList.Add(typeof(CliApp).Assembly.Location);
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (stdin is not null)
        {
            p.StandardInput.BaseStream.Write(stdin, 0, stdin.Length);
            p.StandardInput.Close();
        }
        using var ms = new MemoryStream();
        var errTask = p.StandardError.ReadToEndAsync();
        p.StandardOutput.BaseStream.CopyTo(ms);
        string err = errTask.Result;
        Assert.True(p.WaitForExit(60000));
        return (p.ExitCode, Encoding.UTF8.GetString(ms.ToArray()), err, ms.ToArray());
    }

    /// <summary>
    /// Builds a workbook covering numbers, text with every §8.1 escape class, booleans, formulas, an error and a cycle,
    /// then SAVEs it, LOADs it and SAVEs again. Raw U+0001, U+0008, U+000C, U+001F bytes are spliced into the UTF-8
    /// stdin stream (SETX cannot produce them), alongside non-ASCII text.
    /// </summary>
    private static byte[] BuildScript(string f1, string f2)
    {
        string head = string.Join("\n",
            "SET A1 42",
            "SET A2 -3.5",
            "SET A3 =A1*2+A2",
            "SETX B1 tab\\there",
            "SETX B2 line\\nfeed",
            "SETX B3 carriage\\rreturn",
            "SETX B4 back\\\\slash \"quoted\"",
            "SET B5 ctl");
        string tail = string.Join("\n",
            "",
            "SET C1 " + Unicode,
            "SET C2 =TRUE",
            "SET C3 =1>2",
            "SET C4 =C1&\"!\"",
            "SET D1 =1/0",
            "SET D2 =D1+1",
            "SET E1 =E2",
            "SET E2 =E1",
            "SET E3 =E1+1",
            "DUMP",
            "SAVE " + f1,
            "LOAD " + f1,
            "SAVE " + f2,
            "");
        var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes(head));
        ms.Write(new byte[] { 0x01 });
        ms.Write(Encoding.UTF8.GetBytes("one"));
        ms.Write(new byte[] { 0x08 });
        ms.Write(Encoding.UTF8.GetBytes("bs"));
        ms.Write(new byte[] { 0x0C });
        ms.Write(Encoding.UTF8.GetBytes("ff"));
        ms.Write(new byte[] { 0x1F });
        ms.Write(Encoding.UTF8.GetBytes("us"));
        ms.Write(Encoding.UTF8.GetBytes(tail));
        return ms.ToArray();
    }

    private (string dump, byte[] file1, byte[] file2) BuildSaveLoadSave(string tag, bool bom = false)
    {
        string f1 = P(tag + "-1.gridline"), f2 = P(tag + "-2.gridline");
        byte[] script = BuildScript(f1, f2);
        if (bom) script = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(script).ToArray();
        var (code, o, e, _) = Gridline(script, "script");
        Assert.Equal(0, code);
        Assert.Equal("", e);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(f1)!, "*.tmp*")); // no temp file left behind (§8.4)
        return (o, File.ReadAllBytes(f1), File.ReadAllBytes(f2));
    }

    // §8.1 + §10 + §13: build -> SAVE -> LOAD -> SAVE yields byte-identical files.
    [Fact]
    public void Section8_1_BuildSaveLoadSave_FilesAreByteIdentical()
    {
        var (dump, f1, f2) = BuildSaveLoadSave("rt");
        Assert.NotEmpty(f1);
        Assert.Equal(f1, f2);
        Assert.Contains("A1\tN:42\n", dump);
    }

    // §8.1: every escape class appears in the saved bytes, other controls as lowercase \u00xx, no raw controls, no BOM.
    [Fact]
    public void Section8_1_SavedFile_UsesEscapesAndLowercaseUnicodeForControls()
    {
        var (_, f1, _) = BuildSaveLoadSave("esc");
        string json = new UTF8Encoding(false, true).GetString(f1);
        Assert.Contains("\\t", json);
        Assert.Contains("\\n", json);
        Assert.Contains("\\r", json);
        Assert.Contains("\\\\", json);
        Assert.Contains("\\\"", json);
        Assert.Contains("\\b", json);
        Assert.Contains("\\f", json);
        Assert.Contains("\\u0001", json);
        Assert.Contains("\\u001f", json);
        Assert.DoesNotContain("\\u001F", json);
        Assert.DoesNotContain("\\u0008", json);
        Assert.DoesNotContain("\\u000c", json);
        Assert.False(f1.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.DoesNotContain(f1, b => b < 0x20 && b != (byte)'\n');
        Assert.Contains("café", json);
    }

    // §10 + §13: eval --all on the reloaded file prints exactly what DUMP printed; cycle members print E:#CIRC!.
    [Fact]
    public void Section10_EvalAll_MatchesDump_AndCycleMembersAreCirc()
    {
        var (dump, _, _) = BuildSaveLoadSave("ev");
        var (code, o, e, _) = Gridline(null, "eval", P("ev-1.gridline"), "--all");
        Assert.Equal(0, code);
        Assert.Equal("", e);
        Assert.Equal(dump, o); // SAVE and LOAD print nothing, so the session stdout is exactly the DUMP
        Assert.Contains("E1\tE:#CIRC!\n", o);
        Assert.Contains("E2\tE:#CIRC!\n", o);
        Assert.Contains("D1\tE:#DIV/0!\n", o);
    }

    // §10 + §13: set then eval reflects the change, and re-saving through script LOAD/SAVE is byte-identical (canonical).
    [Fact]
    public void Section10_SetThenEval_ReflectsChange_AndFileStaysCanonical()
    {
        BuildSaveLoadSave("set");
        string file = P("set-1.gridline");
        var (c1, o1, e1, _) = Gridline(null, "set", file, "A1", "100");
        Assert.Equal(0, c1);
        Assert.Equal("", o1);
        Assert.Equal("", e1);
        var (c2, o2, _, _) = Gridline(null, "eval", file, "A1", "A3", "E1");
        Assert.Equal(0, c2);
        Assert.Equal("A1\tN:100\nA3\tN:196.5\nE1\tE:#CIRC!\n", o2);

        string resaved = P("set-resaved.gridline");
        var (c3, _, _, _) = Gridline(Encoding.UTF8.GetBytes($"LOAD {file}\nSAVE {resaved}\n"), "script");
        Assert.Equal(0, c3);
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(resaved));
    }

    // §10 (R31): non-ASCII text survives as raw UTF-8 stdin bytes (with a BOM), independent of the console code page.
    [Fact]
    public void Section10_NonAsciiStdin_WithBom_RoundTripsAndEvaluates()
    {
        var (dump, f1, f2) = BuildSaveLoadSave("bom", bom: true);
        Assert.Equal(f1, f2);
        Assert.Contains("C1\tT:" + Unicode + "\n", dump);
        var (code, o, _, ob) = Gridline(null, "eval", P("bom-1.gridline"), "C1", "C4");
        Assert.Equal(0, code);
        Assert.Equal("C1\tT:" + Unicode + "\nC4\tT:" + Unicode + "!\n", o);
        Assert.Equal(Encoding.UTF8.GetBytes(o), ob);
    }
}
