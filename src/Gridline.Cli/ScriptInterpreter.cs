using System.Text;
using Gridline.Core;
using Gridline.IO;

namespace Gridline.Cli;

/// <summary>
/// Executes script lines (§10.3, R31). Line grammar: blank (empty or only spaces/tabs) and lines starting with '#' are
/// skipped; otherwise the command word is everything up to the first space (case-insensitive) and the rest is the
/// argument text. A leading space therefore gives an empty command word, which is "not implemented".
/// Lines are split on LF only and one trailing CR is stripped (R31).
/// </summary>
internal sealed class ScriptInterpreter(Workbook initial, CliContext ctx)
{
    private Workbook _wb = initial;

    public bool AnyError { get; private set; }

    public void Execute(string line)
    {
        if (line.Length > 0 && line[^1] == '\r') line = line[..^1]; // R31 (no-op if the reader already split CRLF)
        if (IsBlank(line) || line[0] == '#') return;

        int sp = line.IndexOf(' ');
        string word = sp < 0 ? line : line[..sp];
        string rest = sp < 0 ? "" : line[(sp + 1)..];
        try
        {
            if (!Dispatch(word.ToUpperInvariant(), rest)) Fail("not implemented");
        }
        catch (CliException ex) { Fail(ex.Message); }
        catch (GridlineIOException ex) { Fail(ex.Message); }
        catch (GridlineFormatException ex) { Fail(ex.Message); }
        catch (ArgumentException ex) { Fail(ex.Message); }
    }

    private bool Dispatch(string word, string rest)
    {
        switch (word)
        {
            case "SET": Set(rest, unescape: false); return true;
            case "SETX": Set(rest, unescape: true); return true;
            case "GET":
                { string a = OneAddress(rest); ctx.Out(a + "\t" + _wb.GetValue(a).ToCanonicalString() + "\n"); }
                return true;
            case "INPUT":
                { string a = OneAddress(rest); ctx.Out(a + "\t" + CellValue.EscapeText(_wb.GetInput(a)) + "\n"); }
                return true;
            case "DUMP":
                NoArgs(word, rest);
                foreach (string a in _wb.GetUsedAddresses())
                    ctx.Out(a + "\t" + _wb.GetValue(a).ToCanonicalString() + "\n");
                return true;
            case "STATS":
                NoArgs(word, rest);
                ctx.Out("EVALUATED\t" + _wb.LastRecalc.EvaluatedFormulaCount + "\n");
                return true;
            case "SAVE":
                GridlineFile.SaveToPath(_wb, Path(word, rest));
                return true;
            case "LOAD":
                _wb = GridlineFile.LoadFromPath(Path(word, rest)); // assigned only on success
                return true;
            default: return false;
        }
    }

    private void Set(string rest, bool unescape)
    {
        int sp = rest.IndexOf(' ');
        string addrText = sp < 0 ? rest : rest[..sp];
        string input = sp < 0 ? "" : rest[(sp + 1)..];
        if (addrText.Length == 0) throw new CliException(ExitCodes.Usage, "missing cell address");
        string addr = CellArgs.Canonical(addrText);
        if (unescape) input = Unescape(input);
        CellArgs.CheckInputLength(input);
        _wb.SetCell(addr, input);
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\') { sb.Append(c); continue; }
            if (i + 1 >= s.Length) throw new CliException(ExitCodes.Usage, "trailing backslash in SETX input");
            char n = s[++i];
            switch (n)
            {
                case '\\': sb.Append('\\'); break;
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                default: throw new CliException(ExitCodes.Usage, $"unknown escape '\\{n}' in SETX input");
            }
        }
        return sb.ToString();
    }

    private static string OneAddress(string rest)
    {
        if (rest.Length == 0) throw new CliException(ExitCodes.Usage, "missing cell address");
        if (rest.Contains(' ')) throw new CliException(ExitCodes.Usage, "too many arguments");
        return CellArgs.Canonical(rest);
    }

    private static void NoArgs(string word, string rest)
    {
        if (rest.Length != 0) throw new CliException(ExitCodes.Usage, $"{word} takes no arguments");
    }

    private static string Path(string word, string rest)
    {
        if (rest.Length == 0) throw new CliException(ExitCodes.Usage, $"{word} requires a path");
        return rest;
    }

    private static bool IsBlank(string line)
    {
        foreach (char c in line) if (c is not (' ' or '\t')) return false;
        return true;
    }

    private void Fail(string message)
    {
        AnyError = true;
        ctx.Out("ERR\t" + CliContext.OneLine(message) + "\n");
    }
}
