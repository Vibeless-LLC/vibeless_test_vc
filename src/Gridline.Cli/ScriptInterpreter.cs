using System.Text;
using Gridline.Core;
using Gridline.Core.Addressing;
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

    // R40 (§10.3): non-null while a BEGIN block is open; holds the validated SET/SETX edits in order.
    private List<KeyValuePair<string, string>>? _block;

    public bool AnyError { get; private set; }

    /// <summary>End of input (R40): an open BEGIN block is reported and its batch discarded.</summary>
    public void Finish()
    {
        if (_block is null) return;
        _block = null;
        Fail("unterminated BEGIN block");
    }

    /// <summary>Reports a line that could not be executed (e.g. over the length cap) as an ERR line.</summary>
    public void RejectLine(string message) => Fail(message);

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
        if (_block is not null) return DispatchInBlock(word, rest);
        switch (word)
        {
            case "SET": { var (a, i) = ParseSet(rest, unescape: false); _wb.SetCell(a, i); } return true;
            case "SETX": { var (a, i) = ParseSet(rest, unescape: true); _wb.SetCell(a, i); } return true;
            case "BEGIN": NoArgs(word, rest); _block = []; return true;
            case "END": NoArgs(word, rest); throw new CliException(ExitCodes.Usage, "END without BEGIN");
            case "UNDO": NoArgs(word, rest); ctx.Out(_wb.Undo() ? "OK\n" : "NOOP\n"); return true;
            case "REDO": NoArgs(word, rest); ctx.Out(_wb.Redo() ? "OK\n" : "NOOP\n"); return true;
            case "DIRTY": NoArgs(word, rest); ctx.Out(_wb.IsDirty ? "TRUE\n" : "FALSE\n"); return true;
            case "PASTE":
                {
                    string[] parts = rest.Split(' ');
                    if (rest.Length == 0 || parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                        throw new CliException(ExitCodes.Usage, "PASTE requires <srcRange> <dstAddr>");
                    _wb.CopyPaste(parts[0], parts[1]);
                }
                return true;
            case "INSERTROWS": { var (r, n) = RowArgs(word, rest); Structure(() => _wb.InsertRows(r, n)); } return true;
            case "DELETEROWS": { var (r, n) = RowArgs(word, rest); Structure(() => _wb.DeleteRows(r, n)); } return true;
            case "INSERTCOLS": { var (c, n) = ColumnArgs(word, rest); Structure(() => _wb.InsertColumns(c, n)); } return true;
            case "DELETECOLS": { var (c, n) = ColumnArgs(word, rest); Structure(() => _wb.DeleteColumns(c, n)); } return true;
            case "NAME":
                {
                    string[] parts = rest.Split(' ');
                    if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                        throw new CliException(ExitCodes.Usage, "NAME requires <name> <refersTo>");
                    _wb.DefineName(parts[0], parts[1]);
                }
                return true;
            case "UNNAME":
                {
                    if (rest.Length == 0 || rest.Contains(' '))
                        throw new CliException(ExitCodes.Usage, "UNNAME requires <name>");
                    _wb.DeleteName(rest);
                }
                return true;
            case "NAMES":
                NoArgs(word, rest);
                foreach (var kv in _wb.GetNames()) ctx.Out("NAME\t" + kv.Key + "\t" + kv.Value + "\n");
                return true;
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
                _wb.MarkSaved(); // §10.3 "From Phase 2 on": only after a successful save
                return true;
            case "LOAD":
                _wb = GridlineFile.LoadFromPath(Path(word, rest)); // assigned only on success
                return true;
            default: return false;
        }
    }

    // R40: inside BEGIN only SET/SETX (validated exactly as outside a block, then buffered) and END are accepted.
    private bool DispatchInBlock(string word, string rest)
    {
        switch (word)
        {
            case "SET": case "SETX":
                _block!.Add(ToPair(ParseSet(rest, unescape: word == "SETX")));
                return true;
            case "END":
                NoArgs(word, rest);
                var batch = _block!;
                _block = null; // the block is closed even if ApplyBatch throws
                if (batch.Count > 0) _wb.ApplyBatch(batch);
                return true;
            case "BEGIN": NoArgs(word, rest); throw new CliException(ExitCodes.Usage, "BEGIN inside an open BEGIN block");
            default: throw new CliException(ExitCodes.Usage, "command not allowed inside BEGIN block");
        }
    }

    private static KeyValuePair<string, string> ToPair((string addr, string input) e) => new(e.addr, e.input);

    private static (string addr, string input) ParseSet(string rest, bool unescape)
    {
        int sp = rest.IndexOf(' ');
        string addrText = sp < 0 ? rest : rest[..sp];
        string input = sp < 0 ? "" : rest[(sp + 1)..];
        if (addrText.Length == 0) throw new CliException(ExitCodes.Usage, "missing cell address");
        string addr = CellArgs.Canonical(addrText);
        if (unescape) input = Unescape(input);
        CellArgs.CheckInputLength(input);
        return (addr, input);
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

    // Only the structure edits turn InvalidOperationException (a cell would leave the grid) into a user-facing ERR;
    // anywhere else it is an internal failure and must keep escaping as "internal error" (exit 1).
    private static void Structure(Action edit)
    {
        try { edit(); }
        catch (InvalidOperationException ex) { throw new CliException(ExitCodes.Usage, ex.Message); }
    }

    // R73: <row>/<n> are ASCII decimal digits only (no sign, no spaces), and must fit an int.
    private static (int row, int count) RowArgs(string word, string rest)
    {
        string[] parts = TwoArgs(word, rest, "<row> <n>");
        return (Integer(parts[0], "row"), Integer(parts[1], "count"));
    }

    // R73: <col> is 1-2 letters A-ZZ in either case.
    private static (int column, int count) ColumnArgs(string word, string rest)
    {
        string[] parts = TwoArgs(word, rest, "<col> <n>");
        if (!ColumnName.TryParse(parts[0], out int column))
            throw new CliException(ExitCodes.Usage, $"invalid column '{parts[0]}' (expected A to ZZ)");
        return (column, Integer(parts[1], "count"));
    }

    private static string[] TwoArgs(string word, string rest, string shape)
    {
        string[] parts = rest.Split(' ');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            throw new CliException(ExitCodes.Usage, $"{word} requires {shape}");
        return parts;
    }

    private static int Integer(string text, string what)
    {
        foreach (char c in text)
            if (c is < '0' or > '9') throw new CliException(ExitCodes.Usage, $"invalid {what} '{text}' (expected a non-negative integer)");
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int v))
            throw new CliException(ExitCodes.Usage, $"{what} '{text}' is too large");
        return v;
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
