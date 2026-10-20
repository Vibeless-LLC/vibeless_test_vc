using Gridline.Core.Parsing;

namespace Gridline.Core.Tests.Parsing;

// Requirement ID: §5.1 (formula tokenizer).
public class TokenizerTests
{
    private static IReadOnlyList<Token> Ok(string text)
    {
        var r = Tokenizer.Tokenize(text);
        Assert.True(r.Success, r.ErrorMessage);
        return r.Tokens;
    }

    private static void Fails(string text, int? position = null)
    {
        var r = Tokenizer.Tokenize(text);
        Assert.False(r.Success);
        Assert.False(string.IsNullOrEmpty(r.ErrorMessage));
        if (position is { } p) Assert.Equal(p, r.ErrorPosition);
    }

    private static TokenKind[] Kinds(string text) => Ok(text).Select(t => t.Kind).ToArray();

    [Fact]
    public void EmptyInputIsJustEnd() => Assert.Equal([TokenKind.End], Kinds(""));

    [Fact]
    public void WhitespaceIsSkippedBetweenTokens()
    {
        var t = Ok(" 1 \t+\t2 ");
        Assert.Equal([TokenKind.Number, TokenKind.Plus, TokenKind.Number, TokenKind.End], t.Select(x => x.Kind));
        Assert.Equal(1, t[0].Position);
        Assert.Equal(4, t[1].Position);
    }

    [Fact]
    public void FunctionNameImmediatelyBeforeParenIsFunctionUppercased()
    {
        var t = Ok("sum(1)");
        Assert.Equal(TokenKind.Function, t[0].Kind);
        Assert.Equal("SUM", t[0].Text);
        Assert.Equal("sum", t[0].Lexeme);
        Assert.Equal(TokenKind.LParen, t[1].Kind);
    }

    [Fact]
    public void SpaceBeforeParenMakesNameThenLParen()
        => Assert.Equal([TokenKind.Name, TokenKind.LParen, TokenKind.RParen, TokenKind.End], Kinds("SUM ()"));

    [Fact]
    public void TrueParenIsFunction() => Assert.Equal(TokenKind.Function, Ok("TRUE()")[0].Kind);

    [Fact]
    public void CellRefVersusName()
    {
        var a = Ok("a1")[0];
        Assert.Equal(TokenKind.CellRef, a.Kind);
        Assert.Equal(1, a.Reference.Address.Column);
        Assert.Equal(1, a.Reference.Address.Row);
        Assert.Equal("a1", a.Lexeme);
        var f = Ok("foo")[0];
        Assert.Equal(TokenKind.Name, f.Kind);
        Assert.Equal("foo", f.Text);
    }

    [Theory]
    [InlineData("A100001")]
    [InlineData("AAA1")]
    [InlineData("A0")]
    public void OutOfBoundsLookingWordWithoutDollarIsName(string w)
        => Assert.Equal(TokenKind.Name, Ok(w)[0].Kind);

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("False", false)]
    public void Booleans(string w, bool expected)
    {
        var t = Ok(w)[0];
        Assert.Equal(TokenKind.Boolean, t.Kind);
        Assert.Equal(expected, t.Boolean);
    }

    [Fact]
    public void AbsoluteReferenceKeepsFlags()
    {
        var t = Ok("$B$7")[0];
        Assert.Equal(TokenKind.CellRef, t.Kind);
        Assert.True(t.Reference.ColumnAbsolute);
        Assert.True(t.Reference.RowAbsolute);
        Assert.Equal(7, t.Reference.Address.Row);
        Assert.Equal("$B$7", t.Lexeme);
        var m = Ok("B$7")[0].Reference;
        Assert.False(m.ColumnAbsolute);
        Assert.True(m.RowAbsolute);
    }

    [Theory]
    [InlineData("$foo")]
    [InlineData("A$")]
    [InlineData("$A$100001")]
    [InlineData("AAA$1")]
    [InlineData("$")]
    [InlineData("$1")]
    public void BadDollarWordsAreErrors(string w) => Fails(w, 0);

    [Fact]
    public void RangeTokenizesAsRefColonRef()
        => Assert.Equal([TokenKind.CellRef, TokenKind.Colon, TokenKind.CellRef, TokenKind.End], Kinds("A1:B2"));

    [Theory]
    [InlineData("#N/A")]
    [InlineData("#DIV/0!")]
    [InlineData("#NAME?")]
    [InlineData("#VALUE!")]
    [InlineData("#REF!")]
    [InlineData("#NUM!")]
    [InlineData("#CIRC!")]
    [InlineData("#PARSE!")]
    public void ErrorLiteralsWrittenExactly(string src)
    {
        var t = Ok(src)[0];
        Assert.Equal(TokenKind.Error, t.Kind);
        Assert.Equal(src, t.Text);
        Assert.Equal(src, t.Lexeme);
    }

    // R56 (§5.1, supersedes R52): any letter case matches one of the eight codes; the token carries the canonical
    // upper-case code and the lexeme keeps the text as written.
    [Theory]
    [InlineData("#n/a", "#N/A")]
    [InlineData("#div/0!", "#DIV/0!")]
    [InlineData("#Name?", "#NAME?")]
    [InlineData("#Parse!", "#PARSE!")]
    [InlineData("#value!", "#VALUE!")]
    [InlineData("#rEf!", "#REF!")]
    [InlineData("#num!", "#NUM!")]
    [InlineData("#circ!", "#CIRC!")]
    public void ErrorLiteralInOtherCase_IsNormalisedToTheCanonicalCode_R56(string src, string code)
    {
        var t = Ok(src)[0];
        Assert.Equal(TokenKind.Error, t.Kind);
        Assert.Equal(code, t.Text);
        Assert.Equal(src, t.Lexeme);
    }

    [Theory]
    [InlineData("#FOO!")]
    [InlineData("#")]
    [InlineData("#N/AX")]
    [InlineData("#NULL!")]
    [InlineData("#n/ax")]
    [InlineData("#foo!")]
    public void UnknownErrorLiteralIsTokenizerError(string src) => Fails(src, 0);

    // §5.5 printing writes "#N/A / 2" as "#N/A/2": a '/' may end an error literal and is then the divide operator.
    [Theory]
    [InlineData("#N/A/2", "#N/A")]
    [InlineData("#DIV/0!/2", "#DIV/0!")]
    [InlineData("#REF!/A1", "#REF!")]
    [InlineData("#N/A/#N/A", "#N/A")]
    [InlineData("#n/a/2", "#N/A")]          // R56: the bounded '/' backtrack also ignores case
    [InlineData("#Div/0!/2", "#DIV/0!")]
    [InlineData("#ref!/a1", "#REF!")]
    public void ErrorLiteralEndsBeforeDivide(string src, string code)
    {
        var tokens = Ok(src);
        Assert.Equal(TokenKind.Error, tokens[0].Kind);
        Assert.Equal(code, tokens[0].Text);
        Assert.Equal(src[..code.Length], tokens[0].Lexeme);
        Assert.Equal(TokenKind.Slash, tokens[1].Kind);
        Assert.Equal(code.Length, tokens[1].Position);
    }

    [Theory]
    [InlineData("#N/AX/2")]
    [InlineData("#n/ax/2")]
    [InlineData("#DIV/2")]
    [InlineData("#div/2")]
    public void UnknownErrorLiteralBeforeDivideIsStillTokenizerError(string src) => Fails(src, 0);

    // Security review (D-33): the '/' backtrack looks at most ErrorCodes.MaxLength characters and allocates nothing,
    // so a maximum-length '#' + '/' run is linear, not O(n^2).
    [Fact]
    public void LongErrorLiteralRuns_TokenizeInLinearTime()
    {
        string slashes = "#" + new string('/', 32766);
        string naTail = "#N/A" + new string('/', 32763);
        string mixed = "#" + string.Concat(Enumerable.Repeat("A/", 16383));
        string lowerNaTail = "#n/a" + new string('/', 32763); // R56: the case-insensitive backtrack stays bounded
        Tokenizer.Tokenize(slashes); // warm up the JIT
        Tokenizer.Tokenize(naTail);
        Tokenizer.Tokenize(mixed);
        Tokenizer.Tokenize(lowerNaTail);

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r1 = Tokenizer.Tokenize(slashes);
        var r2 = Tokenizer.Tokenize(naTail);
        var r3 = Tokenizer.Tokenize(mixed);
        var r4 = Tokenizer.Tokenize(lowerNaTail);
        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        Assert.False(r1.Success);
        Assert.Equal(0, r1.ErrorPosition);
        Assert.True(r1.ErrorMessage.Length < 100, "the error message echoes a clipped run");
        Assert.True(r2.Success);
        Assert.Equal(TokenKind.Error, r2.Tokens[0].Kind);
        Assert.Equal("#N/A", r2.Tokens[0].Text);
        Assert.Equal(32763, r2.Tokens.Count(t => t.Kind == TokenKind.Slash));
        Assert.False(r3.Success);
        Assert.True(r4.Success);
        Assert.Equal("#N/A", r4.Tokens[0].Text);
        Assert.Equal(32763, r4.Tokens.Count(t => t.Kind == TokenKind.Slash));
        // Linear: the token list and a few strings (a few MB). The old per-k substring backtrack allocated about 1 GB.
        Assert.True(allocated < 16 * 1024 * 1024, $"allocated {allocated} bytes");
        Assert.True(sw.ElapsedMilliseconds < 150, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void StringsAndDoubledQuotes()
    {
        var t = Ok("\"a\"\"b\"")[0];
        Assert.Equal(TokenKind.String, t.Kind);
        Assert.Equal("a\"b", t.Text);
        Assert.Equal("\"a\"\"b\"", t.Lexeme);
        Assert.Equal("", Ok("\"\"")[0].Text);
        Assert.Equal(" x\t ", Ok("\" x\t \"")[0].Text);
    }

    [Fact]
    public void UnterminatedStringIsError() => Fails("1&\"abc", 2);

    [Theory]
    [InlineData("1.50", 1.5)]
    [InlineData("42", 42.0)]
    [InlineData(".5", 0.5)]
    [InlineData("5.", 5.0)]
    [InlineData("1e3", 1000.0)]
    [InlineData("1.5E-2", 0.015)]
    [InlineData("2e+2", 200.0)]
    public void NumbersKeepLexeme(string src, double v)
    {
        var t = Ok(src)[0];
        Assert.Equal(TokenKind.Number, t.Kind);
        Assert.Equal(v, t.Number);
        Assert.Equal(src, t.Lexeme);
    }

    [Fact]
    public void NumberHasNoSignOrPercent()
        => Assert.Equal([TokenKind.Minus, TokenKind.Number, TokenKind.Percent, TokenKind.End], Kinds("-5%"));

    [Fact]
    public void ExponentWithoutDigitsIsNotConsumed()
        => Assert.Equal([TokenKind.Number, TokenKind.Name, TokenKind.End], Kinds("1e"));

    [Fact]
    public void NonFiniteNumberTokenizes_R53()
    {
        var t = Ok("1e999")[0];
        Assert.Equal(TokenKind.Number, t.Kind);
        Assert.Equal(double.PositiveInfinity, t.Number);
        Assert.Equal("1e999", t.Lexeme);                                  // kept for §5.5 printing
    }

    [Fact]
    public void AllOperatorsAndPunctuation()
        => Assert.Equal(
            [TokenKind.Colon, TokenKind.Plus, TokenKind.Minus, TokenKind.Star, TokenKind.Slash, TokenKind.Caret,
             TokenKind.Ampersand, TokenKind.Percent, TokenKind.Equal, TokenKind.LParen, TokenKind.RParen,
             TokenKind.Comma, TokenKind.End],
            Kinds(":+-*/^&%=(),"));

    [Theory]
    [InlineData("<=", TokenKind.LessEqual)]
    [InlineData(">=", TokenKind.GreaterEqual)]
    [InlineData("<>", TokenKind.NotEqual)]
    [InlineData("<", TokenKind.Less)]
    [InlineData(">", TokenKind.Greater)]
    public void ComparisonOperatorsLongestMatch(string src, TokenKind kind)
    {
        var t = Ok(src);
        Assert.Equal([kind, TokenKind.End], t.Select(x => x.Kind));
    }

    [Fact]
    public void SpaceInsideTwoCharOperatorSplits()
        => Assert.Equal([TokenKind.Less, TokenKind.Equal, TokenKind.End], Kinds("< ="));

    [Theory]
    [InlineData("1 @ 2", 2)]
    [InlineData("{1}", 0)]
    [InlineData("A1!", 2)]
    public void UnexpectedCharacterIsError(string src, int pos) => Fails(src, pos);

    [Fact]
    public void EndTokenPositionIsInputLength()
    {
        var t = Ok("A1 ");
        Assert.Equal(3, t[^1].Position);
    }

    [Fact]
    public void NameMayContainDotAndUnderscore()
    {
        var t = Ok("my_name.x1")[0];
        Assert.Equal(TokenKind.Name, t.Kind);
        Assert.Equal("my_name.x1", t.Text);
    }

    [Fact]
    public void NullInputThrows() => Assert.Throws<ArgumentNullException>(() => Tokenizer.Tokenize(null!));
}
