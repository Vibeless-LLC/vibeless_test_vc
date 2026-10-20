using System.Globalization;
using System.Text;
using Gridline.Core.Addressing;

namespace Gridline.Core.Parsing;

/// <summary>
/// Single-pass formula tokenizer (§5.1). Input is the formula text after the leading '='. An error literal is the run of
/// '#' plus letters, digits, '/', '!' and '?', except that a known code followed by '/' ends there and the '/' is the
/// divide operator (D-12, D-33), so the whitespace-free §5.5 form "#N/A/2" re-parses.
/// </summary>
public static class Tokenizer
{
    public static TokenizeResult Tokenize(string formulaText)
    {
        ArgumentNullException.ThrowIfNull(formulaText);
        var s = formulaText;
        var tokens = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == ' ' || c == '\t') { i++; continue; }
            int start = i;

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
            else if (c == '$' || IsAsciiLetter(c))
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
                if (hasDollar)
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
