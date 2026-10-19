using System.Diagnostics;
using System.Text;
using Gridline.Core;
using Gridline.IO;

namespace Gridline.Cli.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridline-cli-" + Guid.NewGuid().ToString("N"));

    public CliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    private string MakeFile(string name, params (string addr, string input)[] cells)
    {
        string path = Path_(name);
        var wb = Workbook.FromCells(cells.Select(c => new KeyValuePair<string, string>(c.addr, c.input)));
        GridlineFile.SaveToPath(wb, path);
        return path;
    }

    private static (int code, byte[] stdout, byte[] stderr) Run(params string[] args)
    {
        var o = new MemoryStream();
        var e = new MemoryStream();
        int code = CliApp.Run(args, new StringReader(""), o, e);
        return (code, o.ToArray(), e.ToArray());
    }

    private static string S(byte[] b) => Encoding.UTF8.GetString(b);

    private static void AssertSingleErrorLine(byte[] stderr)
    {
        string s = S(stderr);
        Assert.StartsWith("error: ", s);
        Assert.EndsWith("\n", s);
        Assert.Equal(1, s.Count(c => c == '\n'));
        Assert.DoesNotContain('\r', s);
    }

    [Fact]
    public void Eval_PrintsCanonicalAddressAndValueInArgumentOrder()
    {
        string f = MakeFile("a.gridline", ("A1", "1"), ("B2", "=A1+1"), ("C3", "hello"));
        var (code, o, e) = Run("eval", f, "c3", "A1", "b2", "D9");
        Assert.Equal(0, code);
        Assert.Equal("C3\tT:hello\nA1\tN:1\nB2\tN:2\nD9\t" + Blank() + "\n", S(o));
        Assert.Empty(e);
    }

    private static string Blank() => new Workbook().GetValue("A1").ToCanonicalString();

    [Fact]
    public void Eval_All_IsRowMajor()
    {
        string f = MakeFile("a.gridline", ("B2", "2"), ("A1", "1"), ("C1", "3"), ("A2", "4"));
        var (code, o, e) = Run("eval", f, "--all");
        Assert.Equal(0, code);
        Assert.Equal("A1\tN:1\nC1\tN:3\nA2\tN:4\nB2\tN:2\n", S(o));
        Assert.Empty(e);
    }

    [Fact]
    public void Eval_All_EmptyWorkbook_PrintsNothing()
    {
        string f = MakeFile("a.gridline");
        var (code, o, e) = Run("eval", f, "--all");
        Assert.Equal(0, code);
        Assert.Empty(o);
        Assert.Empty(e);
    }

    [Fact]
    public void Eval_TextWithTab_IsEscaped()
    {
        string f = MakeFile("a.gridline", ("A1", "'a\tb"));
        var (code, o, _) = Run("eval", f, "A1");
        Assert.Equal(0, code);
        Assert.Equal("A1\tT:a\\tb\n", S(o));
    }

    [Fact]
    public void Eval_ErrorValue()
    {
        string f = MakeFile("a.gridline", ("A1", "=1/0"));
        var (code, o, _) = Run("eval", f, "A1");
        Assert.Equal(0, code);
        Assert.Equal("A1\tE:#DIV/0!\n", S(o));
    }

    [Fact]
    public void Stdout_HasNoBomAndNoCr()
    {
        string f = MakeFile("a.gridline", ("A1", "é€"), ("A2", "x"));
        var (_, o, _) = Run("eval", f, "--all");
        Assert.False(o.Length >= 3 && o[0] == 0xEF && o[1] == 0xBB && o[2] == 0xBF);
        Assert.DoesNotContain((byte)'\r', o);
        Assert.EndsWith("\n", S(o));
    }

    [Fact]
    public void Set_InPlace_UpdatesFile()
    {
        string f = MakeFile("a.gridline", ("A1", "1"));
        var (code, o, e) = Run("set", f, "b1", "=A1*3");
        Assert.Equal(0, code);
        Assert.Empty(o);
        Assert.Empty(e);
        var wb = GridlineFile.LoadFromPath(f);
        Assert.Equal("=A1*3", wb.GetInput("B1"));
        Assert.Equal("N:3", wb.GetValue("B1").ToCanonicalString());
    }

    [Fact]
    public void Set_EmptyInput_ClearsCell()
    {
        string f = MakeFile("a.gridline", ("A1", "1"), ("A2", "2"));
        var (code, _, _) = Run("set", f, "A1", "");
        Assert.Equal(0, code);
        Assert.Equal(new[] { "A2" }, GridlineFile.LoadFromPath(f).GetUsedAddresses());
    }

    [Fact]
    public void Set_InputStartingWithDash_IsTakenVerbatim()
    {
        string f = MakeFile("a.gridline");
        Assert.Equal(0, Run("set", f, "A1", "-5").code);
        Assert.Equal("-5", GridlineFile.LoadFromPath(f).GetInput("A1"));
    }

    [Fact]
    public void Set_WithOut_LeavesSourceUntouched()
    {
        string f = MakeFile("a.gridline", ("A1", "1"));
        byte[] before = File.ReadAllBytes(f);
        string outPath = Path_("b.gridline");
        var (code, o, e) = Run("set", f, "A2", "2", "--out", outPath);
        Assert.Equal(0, code);
        Assert.Empty(o);
        Assert.Empty(e);
        Assert.Equal(before, File.ReadAllBytes(f));
        Assert.Equal(new[] { "A1", "A2" }, GridlineFile.LoadFromPath(outPath).GetUsedAddresses());
    }

    [Theory]
    [InlineData]
    [InlineData("bogus")]
    [InlineData("eval")]
    [InlineData("eval", "f.gridline")]
    [InlineData("eval", "f.gridline", "--all", "A1")]
    [InlineData("eval", "f.gridline", "--nope", "A1")]
    [InlineData("set")]
    [InlineData("set", "f.gridline", "A1")]
    [InlineData("set", "f.gridline", "A1", "x", "extra")]
    [InlineData("set", "f.gridline", "A1", "x", "--out")]
    [InlineData("set", "f.gridline", "A1", "x", "--bad", "p")]
    [InlineData("set", "f.gridline", "A1", "x", "--out", "p", "more")]
    public void UsageErrors_Exit1(params string[] args)
    {
        var (code, o, e) = Run(args);
        Assert.Equal(1, code);
        Assert.Empty(o);
        AssertSingleErrorLine(e);
    }

    [Fact]
    public void Set_OversizedInput_Exit1_AndFileUntouched()
    {
        string f = MakeFile("a.gridline", ("A1", "1"));
        byte[] before = File.ReadAllBytes(f);
        var (code, _, e) = Run("set", f, "A1", new string('x', Workbook.MaxInputLength + 1));
        Assert.Equal(1, code);
        AssertSingleErrorLine(e);
        Assert.Equal(before, File.ReadAllBytes(f));
    }

    [Fact]
    public void MissingFile_Exit2()
    {
        foreach (var args in new[] { new[] { "eval", Path_("nope.gridline"), "A1" }, new[] { "set", Path_("nope.gridline"), "A1", "1" } })
        {
            var (code, o, e) = Run(args);
            Assert.Equal(2, code);
            Assert.Empty(o);
            AssertSingleErrorLine(e);
        }
    }

    [Fact]
    public void MalformedFile_Exit3()
    {
        string f = Path_("bad.gridline");
        File.WriteAllText(f, "{ not json");
        var (code, o, e) = Run("eval", f, "A1");
        Assert.Equal(3, code);
        Assert.Empty(o);
        AssertSingleErrorLine(e);
    }

    [Fact]
    public void InvalidAddress_Exit5_WithEmptyStdout_EvenAfterValidOnes()
    {
        string f = MakeFile("a.gridline", ("A1", "1"));
        var (code, o, e) = Run("eval", f, "A1", "ZZZ99999999", "B2");
        Assert.Equal(5, code);
        Assert.Empty(o);
        AssertSingleErrorLine(e);
        foreach (string bad in new[] { "A1:B2", "0", "A0", "", "1A" })
            Assert.Equal(5, Run("eval", f, bad).code);
        byte[] before = File.ReadAllBytes(f);
        Assert.Equal(5, Run("set", f, "A0", "1").code);
        Assert.Equal(before, File.ReadAllBytes(f));
    }

    [Fact]
    public void ErrorMessagesContainingNewlines_AreCollapsedToOneLine()
    {
        Assert.Equal("a b", Gridline.Cli.CliContext.OneLine("a\r\nb"));
        Assert.Equal("ab", Gridline.Cli.CliContext.OneLine("ab\n"));
    }

    [Fact]
    public void Process_Smoke_ExitCodeAndBytes()
    {
        string f = MakeFile("smoke.gridline", ("A1", "'a\tb"));
        string dll = typeof(CliApp).Assembly.Location;
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("eval");
        psi.ArgumentList.Add(f);
        psi.ArgumentList.Add("A1");
        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        string err = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(30000));
        Assert.Equal(0, p.ExitCode);
        Assert.Equal("", err);
        Assert.Equal(Encoding.UTF8.GetBytes("A1\tT:a\\tb\n"), ms.ToArray());

        psi.ArgumentList.Clear();
        psi.ArgumentList.Add(dll);
        using var p2 = Process.Start(psi)!;
        string err2 = p2.StandardError.ReadToEnd();
        p2.WaitForExit(30000);
        Assert.Equal(1, p2.ExitCode);
        Assert.StartsWith("error: ", err2);
    }
}
