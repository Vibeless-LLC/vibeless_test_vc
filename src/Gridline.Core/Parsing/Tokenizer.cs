using System.Globalization;
using System.Text;
using Gridline.Core.Addressing;

namespace Gridline.Core.Parsing;

/// <summary>
/// Single-pass formula tokenizer (§5.1). Input is the formula text after the leading '='. An error literal is the run of
/// '#' plus letters, digits, '/', '!' and '?', except that a known code followed by '/' ends there and the '/' is the
/// divide operator (D-12, D-33), so the whitespace-free §5.5 form "#N/A/2" re-parses. A sheet prefix (CR1.2, R82),
/// unquoted <c>Data!</c> or quoted <c>'My Sheet'!</c>, must be followed at once by a cell reference; together they form
/// one qualified CellRef token (D-51).
/// </summary>
public static class Tokenizer
{
    public static TokenizeResult Tokenize(string formulaText) => Tokenize(formulaText, out _);

    /// <summary>As <see cref="Tokenize(string)"/>, also reporting <paramref name="prefixScanSteps"/>: the characters examined
    /// by the sheet-prefix lookahead over [A-Za-z0-9_.] runs. Linear tokenizing keeps it ≤ the text length; a regression
    /// that rescans a run per token makes it quadratic. A deterministic test seam (no wall-clock timing).</summary>
    internal static TokenizeResult Tokenize(string formulaText, out long prefixScanSteps)
    {
        ArgumentNullException.ThrowIfNull(formulaText);
        prefixScanSteps = 0;
        var s = formulaText;
        var tokens = new List<Token>();
        int i = 0;
        // End of the last [A-Za-z0-9_.] run found not to be a sheet prefix. A token starting inside it belongs to the same
        // run, so each run is scanned once and tokenizing stays linear (e.g. "1.2.3.4..." is many Number tokens).
        int bareRunEnd = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == ' ' || c == '\t') { i++; continue; }
            int start = i;

            if (c == '\'')
            {
                // R82: a quoted sheet prefix, '' standing for '. It must hold a valid CR1.1 name and be followed by '!'.
                var name = new StringBuilder();
                int k = i + 1;
                bool closed = false;
                while (k < s.Length)
                {
                    if (s[k] == '\'')
                    {
                        if (k + 1 < s.Length && s[k + 1] == '\'') { name.Append('\''); k += 2; continue; }
                        k++;
                        closed = true;
                        break;
                    }
                    name.Append(s[k++]);
                }
                if (!closed) return TokenizeResult.Fail("Unterminated quoted sheet name.", start);
                if (k >= s.Length || s[k] != '!') return TokenizeResult.Fail("A quoted sheet name must be followed by '!'.", k);
                string sheet = name.ToString();
                if (!SheetName.IsValid(sheet))
                    return TokenizeResult.Fail($"Invalid sheet name '{Clip(sheet, 0, sheet.Length)}'.", start);
                if (ReadQualified(s, start, sheet, k + 1, tokens, out i) is { } failure) return failure;
                continue;
            }

            if (i >= bareRunEnd && SheetName.IsBareChar(c))
            {
                int j = i;
                while (j < s.Length && SheetName.IsBareChar(s[j])) j++;
                prefixScanSteps += j - i;
                if (j < s.Length && s[j] == '!')
                {
                    // R82: an unquoted prefix must be a valid sheet name that needs no quotes (so not 2024, A1 or TRUE).
                    // The error is reported at the '!', where the text stops being a number or word (D-12: "A1!" at 2).
                    var sheet = s.AsSpan(i, j - i);
                    if (!SheetName.IsValid(sheet))
                        return TokenizeResult.Fail($"Invalid sheet name '{Clip(s, i, j - i)}'.", j);
                    if (SheetName.RequiresQuoting(sheet))
                        return TokenizeResult.Fail($"Sheet name '{Clip(s, i, j - i)}' must be quoted.", j);
                    if (ReadQualified(s, start, sheet.ToString(), j + 1, tokens, out i) is { } failure) return failure;
                    continue;
                }
                bareRunEnd = j;
            }

            if (IsDigit(c) || (c == '.' && i + 1 < s.Length && IsDigit(s[i + 1])))
            {
                i = ScanNumber(s, i);
                string lex = s.Substring(start, i - start);
                // R53 (§4.4): an overflowing literal such as 1e999 is valid syntax; its value is +inf, which the
                // evaluator turns into #NUM!. The lexeme is kept for §5.5 printing.
                double v = double.Parse(lex, NumberStyles.Float, CultureInfo.InvariantCulture);
                tokens.Add(new Token(TokenKind.Number, start, lex, Number: v));
            }
            else if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                bool closed = false;
                while (i < s.Length)
                {
                    if (s[i] == '"')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        i++;
                        closed = true;
                        break;
                    }
                    sb.Append(s[i++]);
                }
                if (!closed) return TokenizeResult.Fail("Unterminated string literal.", start);
                tokens.Add(new Token(TokenKind.String, start, s.Substring(start, i - start), Text: sb.ToString()));
            }
            else if (c == '#')
            {
                i++;
                while (i < s.Length && (IsAsciiLetter(s[i]) || IsDigit(s[i]) || s[i] is '/' or '!' or '?')) i++;
                // §5.5 printing emits no whitespace, so "#N/A / 2" prints as "#N/A/2": a '/' may end a literal and be the
                // divide operator (D-33). When the whole run is unknown, take the longest known code that ends just before a
                // '/' in the run. Only the first ErrorCodes.MaxLength characters can hold one, so this is O(1) per token and
                // allocation-free; a 32,767-character "#////..." run stays linear. R56 (§5.1, supersedes R52): codes match
                // ignoring case and the token carries the canonical upper-case code, so "#n/a/2" is #N/A, Slash, 2.
                int end = i;
                if (!ErrorCodes.TryGetCanonical(s.AsSpan(start, end - start), out string code))
                {
                    for (int k = Math.Min(end - start - 1, ErrorCodes.MaxLength); k > 0; k--)
                    {
                        if (s[start + k] == '/' && ErrorCodes.TryGetCanonical(s.AsSpan(start, k), out code)) { end = start + k; break; }
                    }
                    if (end == i)
                        return TokenizeResult.Fail($"Unknown error literal '{Clip(s, start, i - start)}'.", start);
                }
                i = end;
                tokens.Add(new Token(TokenKind.Error, start, s.Substring(start, end - start), Text: code));
            }
            else if (c == '$' || c == '_' || IsAsciiLetter(c))
            {
                i++;
                bool hasDollar = c == '$';
                while (i < s.Length && (IsAsciiLetter(s[i]) || IsDigit(s[i]) || s[i] is '_' or '.' or '$'))
                {
                    if (s[i] == '$') hasDollar = true;
                    i++;
                }
                string lex = s.Substring(start, i - start);
                bool isRef = CellReference.TryParse(lex.AsSpan(), out var reference);
                if (c == '_' && !hasDollar)
                {
                    // R57 (§5.1, §9.6): a '_'-initial word can only be a name reference; followed by '(' the parser
                    // rejects it like any name, and one containing '$' falls through to the #PARSE! rule below.
                    tokens.Add(new Token(TokenKind.Name, start, lex, Text: lex));
                }
                else if (hasDollar)
                {
                    if (!isRef) return TokenizeResult.Fail($"Invalid cell reference '{lex}'.", start);
                    tokens.Add(new Token(TokenKind.CellRef, start, lex, Reference: reference));
                }
                else if (i < s.Length && s[i] == '(')
                {
                    tokens.Add(new Token(TokenKind.Function, start, lex, Text: lex.ToUpperInvariant()));
                }
                else if (isRef)
                {
                    tokens.Add(new Token(TokenKind.CellRef, start, lex, Reference: reference));
                }
                else if (lex.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
                {
                    tokens.Add(new Token(TokenKind.Boolean, start, lex, Boolean: true));
                }
                else if (lex.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                {
                    tokens.Add(new Token(TokenKind.Boolean, start, lex, Boolean: false));
                }
                else
                {
                    tokens.Add(new Token(TokenKind.Name, start, lex, Text: lex));
                }
            }
            else
            {
                TokenKind kind;
                int len = 1;
                switch (c)
                {
                    case ':': kind = TokenKind.Colon; break;
                    case '+': kind = TokenKind.Plus; break;
                    case '-': kind = TokenKind.Minus; break;
                    case '*': kind = TokenKind.Star; break;
                    case '/': kind = TokenKind.Slash; break;
                    case '^': kind = TokenKind.Caret; break;
                    case '&': kind = TokenKind.Ampersand; break;
                    case '%': kind = TokenKind.Percent; break;
                    case '=': kind = TokenKind.Equal; break;
                    case '(': kind = TokenKind.LParen; break;
                    case ')': kind = TokenKind.RParen; break;
                    case ',': kind = TokenKind.Comma; break;
                    case '<':
                        if (i + 1 < s.Length && s[i + 1] == '=') { kind = TokenKind.LessEqual; len = 2; }
                        else if (i + 1 < s.Length && s[i + 1] == '>') { kind = TokenKind.NotEqual; len = 2; }
                        else kind = TokenKind.Less;
                        break;
                    case '>':
                        if (i + 1 < s.Length && s[i + 1] == '=') { kind = TokenKind.GreaterEqual; len = 2; }
                        else kind = TokenKind.Greater;
                        break;
                    default:
                        return TokenizeResult.Fail($"Unexpected character '{c}'.", i);
                }
                i += len;
                tokens.Add(new Token(kind, start, s.Substring(start, len)));
            }
        }
        tokens.Add(new Token(TokenKind.End, s.Length, ""));
        return TokenizeResult.Ok(tokens);
    }

    /// <summary>
    /// Reads the cell reference that must follow a sheet prefix's '!' at <paramref name="at"/> with no whitespace (R82)
    /// and adds one qualified CellRef token from <paramref name="start"/> to its end. A name, a function, a space or
    /// anything else after the '!' is an error; a following ':' range is left to the parser.
    /// </summary>
    /// <returns>The failure, or null with <paramref name="next"/> set to the index after the reference.</returns>
    private static TokenizeResult? ReadQualified(string s, int start, string sheet, int at, List<Token> tokens, out int next)
    {
        int end = at;
        while (end < s.Length && (IsAsciiLetter(s[end]) || IsDigit(s[end]) || s[end] is '_' or '.' or '$')) end++;
        next = end;
        if (!CellReference.TryParse(s.AsSpan(at, end - at), out var reference))
            return TokenizeResult.Fail($"Expected a cell reference after '{Clip(s, start, at - start)}'.", at);
        tokens.Add(new Token(TokenKind.CellRef, start, s.Substring(start, end - start), Reference: reference, Sheet: sheet));
        return null;
    }

    // digits ['.' digits*] [exp] | '.' digits [exp]; exp = [eE][+-]?digits (consumed only when digits follow).
    private static int ScanNumber(string s, int i)
    {
        while (i < s.Length && IsDigit(s[i])) i++;
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && IsDigit(s[i])) i++;
        }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < s.Length && (s[j] == '+' || s[j] == '-')) j++;
            if (j < s.Length && IsDigit(s[j]))
            {
                while (j < s.Length && IsDigit(s[j])) j++;
                i = j;
            }
        }
        return i;
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';
    private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    /// <summary>The run for an error message, cut to 32 characters so a huge bad literal does not echo in full.</summary>
    private static string Clip(string s, int start, int length)
        => length <= 32 ? s.Substring(start, length) : string.Concat(s.AsSpan(start, 32), "...");
}
