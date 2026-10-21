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
/// Arguments are split on single spaces, except that a token starting with <c>'</c> runs to its closing quote
/// (<c>''</c> stands for <c>'</c>) before the next space ends it, so quoted sheet names and qualified addresses may
/// contain spaces (CR1.7, R87).
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
                    var parts = Args(word, rest, 2, 2, "<srcRange> <dstAddr>");
                    _wb.CopyPaste(parts[0], parts[1]); // R83: either may be qualified
                }
                return true;
            case "INSERTROWS":
                {
                    var (s, r, n) = RowArgs(word, rest);
                    Guarded(() => { if (s is null) _wb.InsertRows(r, n); else _wb.InsertRows(s, r, n); });
                }
                return true;
            case "DELETEROWS":
                {
                    var (s, r, n) = RowArgs(word, rest);
                    Guarded(() => { if (s is null) _wb.DeleteRows(r, n); else _wb.DeleteRows(s, r, n); });
                }
                return true;
            case "INSERTCOLS":
                {
                    var (s, c, n) = ColumnArgs(word, rest);
                    Guarded(() => { if (s is null) _wb.InsertColumns(c, n); else _wb.InsertColumns(s, c, n); });
                }
                return true;
            case "DELETECOLS":
                {
                    var (s, c, n) = ColumnArgs(word, rest);
                    Guarded(() => { if (s is null) _wb.DeleteColumns(c, n); else _wb.DeleteColumns(s, c, n); });
                }
                return true;
            case "ADDSHEET":
                {
                    var t = Args(word, rest, 1, 2, "<name> [index]");
                    string name = SheetNameArg(t[0]);
                    int? index = t.Count == 2 ? Integer(t[1], "index") : null;
                    Guarded(() => _wb.AddSheet(name, index));
                }
                return true;
            case "RENAMESHEET":
                {
                    var t = Args(word, rest, 2, 2, "<old> <new>");
                    string oldName = SheetNameArg(t[0]), newName = SheetNameArg(t[1]);
                    Guarded(() => _wb.RenameSheet(oldName, newName));
                }
                return true;
            case "DELETESHEET":
                {
                    string name = SheetNameArg(Args(word, rest, 1, 1, "<name>")[0]);
                    Guarded(() => _wb.DeleteSheet(name));
                }
                return true;
            case "MOVESHEET":
                {
                    var t = Args(word, rest, 2, 2, "<name> <index>");
                    string name = SheetNameArg(t[0]);
                    int index = Integer(t[1], "index");
                    Guarded(() => _wb.MoveSheet(name, index));
                }
                return true;
            case "ACTIVATE":
                _wb.ActiveSheet = SheetNameArg(Args(word, rest, 1, 1, "<name>")[0]);
                return true;
            case "SHEETS":
                {
                    NoArgs(word, rest);
                    var names = _wb.SheetNames;
                    for (int i = 0; i < names.Count; i++)
                        ctx.Out("SHEET\t" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" + names[i] + "\n");
                    ctx.Out("ACTIVE\t" + _wb.ActiveSheet + "\n");
                }
                return true;
            case "NAME":
                {
                    var parts = Args(word, rest, 2, 2, "<name> <refersTo>");
                    _wb.DefineName(parts[0], parts[1]); // R77: the refersTo may be qualified, e.g. 'My Sheet'!$A$1
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
                foreach (string a in _wb.GetUsedAddresses()) // CR1.3: unqualified with one sheet, else qualified
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

    // R40, R87: inside BEGIN only SET/SETX (validated exactly as outside a block, then buffered) and END are accepted;
    // every other command, including the sheet commands and ACTIVATE, is an ERR.
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

    // R87: the address token is quote-aware (SET 'My Sheet'!A1 x y); the input is the rest of the line after one space.
    private (string addr, string input) ParseSet(string rest, bool unescape)
    {
        int end = TokenEnd(rest, 0);
        string addrText = rest[..end];
        string input = end < rest.Length ? rest[(end + 1)..] : "";
        if (addrText.Length == 0) throw new CliException(ExitCodes.Usage, "missing cell address");
        AddressArg parsed = CellArgs.ParseAddress(addrText);
        if (unescape) input = Unescape(input);
        CellArgs.CheckInputLength(input);
        return (CellArgs.Resolve(_wb, parsed), input);
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

    // GET and INPUT: one quote-aware address token, echoed canonically, qualified exactly when given qualified (R87).
    private string OneAddress(string rest)
    {
        if (rest.Length == 0) throw new CliException(ExitCodes.Usage, "missing cell address");
        var tokens = Split(rest);
        if (tokens.Count != 1) throw new CliException(ExitCodes.Usage, "too many arguments");
        return CellArgs.Resolve(_wb, CellArgs.ParseAddress(tokens[0]));
    }

    // The structure edits and sheet operations turn InvalidOperationException (a cell would leave the grid, deleting the
    // last sheet, a 256th sheet, a rename rewrite over the input limit) into a user-facing ERR; anywhere else it is an
    // internal failure and must keep escaping as "internal error" (exit 1).
    private static void Guarded(Action edit)
    {
        try { edit(); }
        catch (InvalidOperationException ex) { throw new CliException(ExitCodes.Usage, ex.Message); }
    }

    // R73, R87: [<sheet>!]<row> <n>; <row>/<n> are ASCII decimal digits only (no sign, no spaces), and must fit an int.
    private static (string? sheet, int row, int count) RowArgs(string word, string rest)
    {
        var parts = Args(word, rest, 2, 2, "[<sheet>!]<row> <n>");
        var (sheet, row) = SheetPrefix(parts[0]);
        return (sheet, Integer(row, "row"), Integer(parts[1], "count"));
    }

    // R73, R87: [<sheet>!]<col> <n>; <col> is 1-2 letters A-ZZ in either case.
    private static (string? sheet, int column, int count) ColumnArgs(string word, string rest)
    {
        var parts = Args(word, rest, 2, 2, "[<sheet>!]<col> <n>");
        var (sheet, col) = SheetPrefix(parts[0]);
        if (!ColumnName.TryParse(col, out int column))
            throw new CliException(ExitCodes.Usage, $"invalid column '{col}' (expected A to ZZ)");
        return (sheet, column, Integer(parts[1], "count"));
    }

    // The R82 prefix rules (CellArgs); whether the sheet exists is the workbook's check (an ERR).
    private static (string? sheet, string rest) SheetPrefix(string token)
    {
        if (!CellArgs.TrySplitQualified(token, out string? sheet, out string rest))
            throw new CliException(ExitCodes.Usage, $"invalid sheet prefix in '{token}'");
        return (sheet, rest);
    }

    /// <summary>
    /// A sheet-name argument under the CR1.2 quoting rules (R87): either <c>'…'</c> with <c>''</c> for <c>'</c> (so it may
    /// contain spaces), or a bare name that needs no quoting. Whether the name is valid for the operation, exists or is
    /// free is the workbook's check.
    /// </summary>
    private static string SheetNameArg(string token)
    {
        if (token[0] != '\'')
        {
            if (!SheetName.IsValid(token)) throw new CliException(ExitCodes.Usage, $"invalid sheet name '{token}'");
            if (SheetName.RequiresQuoting(token))
                throw new CliException(ExitCodes.Usage, $"sheet name '{token}' must be quoted");
            return token;
        }
        if (!CellArgs.TryReadQuoted(token, out string name, out int end) || end != token.Length)
            throw new CliException(ExitCodes.Usage, $"invalid quoted sheet name {token}");
        return name;
    }

    /// <summary>Splits the argument text (see the class summary) into <paramref name="min"/> to <paramref name="max"/> non-empty tokens.</summary>
    private static List<string> Args(string word, string rest, int min, int max, string shape)
    {
        var tokens = Split(rest);
        if (tokens.Count < min || tokens.Count > max || tokens.Exists(t => t.Length == 0))
            throw new CliException(ExitCodes.Usage, $"{word} requires {shape}");
        return tokens;
    }

    /// <summary>R31, R87: tokens separated by single spaces, so a double or trailing space gives an empty token; "" gives none.</summary>
    private static List<string> Split(string rest)
    {
        var tokens = new List<string>();
        if (rest.Length == 0) return tokens;
        int start = 0;
        while (true)
        {
            int end = TokenEnd(rest, start);
            tokens.Add(rest[start..end]);
            if (end >= rest.Length) return tokens;
            start = end + 1;
        }
    }

    /// <summary>The index of the space (or the end of the text) that ends the token at <paramref name="start"/>. A token
    /// starting with <c>'</c> first runs to its closing quote, skipping <c>''</c> pairs.</summary>
    private static int TokenEnd(string s, int start)
    {
        int i = start;
        if (i < s.Length && s[i] == '\'')
        {
            if (!CellArgs.TryReadQuoted(s[start..], out _, out int end))
                throw new CliException(ExitCodes.Usage, "unterminated quoted sheet name");
            i = start + end;
        }
        int sp = s.IndexOf(' ', i);
        return sp < 0 ? s.Length : sp;
    }

    private static int Integer(string text, string what)
    {
        if (text.Length == 0) throw new CliException(ExitCodes.Usage, $"missing {what}"); // e.g. INSERTROWS Data! 2
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
