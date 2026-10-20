using System.Text;
using FsCheck;
using FsCheck.Fluent;
using Gridline.Core.Addressing;
using Gridline.Core.Parsing;

namespace Gridline.Core.Tests.Parsing;

// Requirement IDs: CR1.2 (qualified references Sheet!A1 and Sheet!A1:B3, quoting, #PARSE! cases), §5.5 as amended by
// CR1.2 (sheet in defined casing, quoted only when required), R82 (prefix rules), §6.7 / R10 (no recursion).
public class QualifiedReferenceTests
{
    private static ParsedFormula Parse(string formula) => FormulaParser.Parse(formula.StartsWith('=') ? formula[1..] : formula, null);

    private static FormulaNode Root(string formula)
    {
        var parsed = Parse(formula);
        Assert.True(parsed.Success, $"'{formula}': {parsed.ErrorMessage} at {parsed.ErrorPosition}");
        return parsed.Root!;
    }

    private static string P(string formula, IReferenceRewriter? rewriter = null, ISheetCasingResolver? sheets = null)
        => FormulaPrinter.Print(Root(formula), rewriter, null, sheets);

    private sealed class Sheets(params string[] defined) : ISheetCasingResolver
    {
        public string? GetDefinedCasing(string sheet)
            => defined.FirstOrDefault(d => string.Equals(d, sheet, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Tokenizer ----

    [Fact]
    public void UnquotedPrefix_IsOneQualifiedCellRefToken()
    {
        var tokens = Tokenizer.Tokenize("Data!$B$2*2").Tokens;
        Assert.Equal([TokenKind.CellRef, TokenKind.Star, TokenKind.Number, TokenKind.End], tokens.Select(t => t.Kind));
        Assert.Equal("Data", tokens[0].Sheet);
        Assert.Equal("Data!$B$2", tokens[0].Lexeme);
        Assert.Equal(CellReference.Parse("$B$2"), tokens[0].Reference);
        Assert.Equal(0, tokens[0].Position);
        Assert.Null(tokens[2].Sheet);
    }

    [Fact]
    public void QuotedPrefix_CollapsesDoubledQuotes()
    {
        var t = Tokenizer.Tokenize(" 'O''Brien'!a1").Tokens[0];
        Assert.Equal(TokenKind.CellRef, t.Kind);
        Assert.Equal("O'Brien", t.Sheet);
        Assert.Equal("'O''Brien'!a1", t.Lexeme);
        Assert.Equal(1, t.Position);
    }

    [Fact]
    public void UnqualifiedReference_HasNoSheet()
    {
        var t = Tokenizer.Tokenize("A1").Tokens[0];
        Assert.Equal(TokenKind.CellRef, t.Kind);
        Assert.Null(t.Sheet);
    }

    [Fact]
    public void LongNumberRuns_StillTokenizeInLinearTime()
    {
        // Each token start inside a [A-Za-z0-9_.] run reuses the run's end, so "1.2.3..." is not rescanned per token.
        string dotted = string.Concat(Enumerable.Repeat("1.", 16383)) + "1";
        string words = string.Concat(Enumerable.Repeat("a.", 16383));
        Tokenizer.Tokenize(dotted); // warm up the JIT
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r1 = Tokenizer.Tokenize(dotted);
        var r2 = Tokenizer.Tokenize(words);
        sw.Stop();
        Assert.True(r1.Success);
        Assert.True(r2.Success);
        Assert.True(sw.ElapsedMilliseconds < 150, $"took {sw.ElapsedMilliseconds} ms");
    }

    // ---- Parser and AST ----

    [Fact]
    public void QualifiedCell_KeepsTheSheetAsWritten()
    {
        var c = Assert.IsType<CellRefNode>(Root("=data!$a1"));
        Assert.Equal("data", c.Sheet);
        Assert.Equal(CellReference.Parse("$A1"), c.Reference);
    }

    [Fact]
    public void QualifiedRange_HasOneSheetForBothCorners()
    {
        var r = Assert.IsType<RangeNode>(Root("='My Sheet'!B3:$a$1"));
        Assert.Equal("My Sheet", r.Sheet);
        Assert.Equal(CellReference.Parse("B3"), r.Start);
        Assert.Equal(CellReference.Parse("$A$1"), r.End);
    }

    [Fact]
    public void UnqualifiedNodes_HaveNullSheet()
    {
        Assert.Null(Assert.IsType<CellRefNode>(Root("=A1")).Sheet);
        Assert.Null(Assert.IsType<RangeNode>(Root("=A1:B2")).Sheet);
    }

    [Fact]
    public void QualifiedReferences_WorkAsOperandsAndArguments()
    {
        var call = Assert.IsType<FunctionCallNode>(Root("=SUM(Data!A1:A3, 'x y'!B1, C1) "));
        Assert.Equal("Data", Assert.IsType<RangeNode>(call.Arguments[0]).Sheet);
        Assert.Equal("x y", Assert.IsType<CellRefNode>(call.Arguments[1]).Sheet);
        Assert.Null(Assert.IsType<CellRefNode>(call.Arguments[2]).Sheet);
        Assert.Equal("=-Data!A1%+(Data!B1:C2)", P("= - Data!A1 % + ( Data!B1 : C2 )"));
    }

    [Fact]
    public void Nodes_RejectAnInvalidSheetName()
    {
        Assert.Throws<ArgumentException>(() => new CellRefNode(CellReference.Parse("A1"), "a[b"));
        Assert.Throws<ArgumentException>(() => new RangeNode(CellReference.Parse("A1"), CellReference.Parse("B2"), ""));
    }

    // CR1.2 and R82: each listed input is #PARSE!.
    [Theory]
    [InlineData("Sheet2!A1:Sheet2!B3")]  // a second prefix
    [InlineData("My Sheet!A1")]          // a name needing quotes, unquoted
    [InlineData("2024!A1")]
    [InlineData("A1!B2")]
    [InlineData("abc123!A1")]
    [InlineData("TRUE!A1")]
    [InlineData("false!A1")]
    [InlineData("Data!Rate")]            // a name after '!'
    [InlineData("Data!SUM(A1)")]         // a function call after '!'
    [InlineData("Data!A1(1)")]
    [InlineData("Data !A1")]             // whitespace around '!'
    [InlineData("Data! A1")]
    [InlineData("'Data' !A1")]
    [InlineData("'Data'! A1")]
    [InlineData("A1:Data!B2")]           // a prefix on the second corner only
    [InlineData("Data!A1:'Data'!B2")]
    [InlineData("'a[b'!A1")]             // a quoted name breaking CR1.1
    [InlineData("''!A1")]                // empty name
    [InlineData("'''x'!A1")]             // begins with '
    [InlineData("'12345678901234567890123456789012'!A1")]
    [InlineData("abcdefghijklmnopqrstuvwxyz123456!A1")] // 32 characters, unquoted
    [InlineData("'My Sheet!A1")]         // unterminated quote
    [InlineData("'My Sheet'")]           // a quoted name without '!'
    [InlineData("'My Sheet'A1")]
    [InlineData("Data!")]
    [InlineData("Data!!A1")]
    [InlineData("Data!A0")]
    [InlineData("Data!AAA1")]
    [InlineData("Data!A1:Rate")]
    [InlineData("Data!A1:B2:C3")]
    [InlineData("$Data!A1")]
    [InlineData("Data$!A1")]
    [InlineData("Data!#REF!")]
    public void InvalidQualifiedSyntax_IsParseError(string formula)
    {
        var parsed = Parse(formula);
        Assert.False(parsed.Success, $"'{formula}' parsed");
        Assert.False(string.IsNullOrEmpty(parsed.ErrorMessage));
        Assert.InRange(parsed.ErrorPosition, 0, formula.Length);
    }

    [Theory]
    [InlineData("_x!A1", "_x")]
    [InlineData("a.b!A1", "a.b")]
    [InlineData(".5abc!A1", ".5abc")]
    [InlineData("Sheet1!A1", "Sheet1")]
    [InlineData("abcdefghijklmnopqrstuvwxyz12345!A1", "abcdefghijklmnopqrstuvwxyz12345")] // 31 characters
    [InlineData("'2024'!A1", "2024")]
    [InlineData("'A1'!B2", "A1")]
    [InlineData("'TRUE'!A1", "TRUE")]
    [InlineData("'a!b'!A1", "a!b")]
    [InlineData("'Été'!A1", "Été")]
    public void ValidPrefixes_Parse(string formula, string sheet)
        => Assert.Equal(sheet, Assert.IsType<CellRefNode>(Root(formula)).Sheet);

    [Theory]
    [InlineData("A1!B2", 2)]          // an unquoted prefix needing quotes: at the '!' (as D-12's "A1!")
    [InlineData("1+2024!A1", 6)]
    [InlineData("Data!Rate", 5)]      // not a cell reference after the '!'
    [InlineData("Data! A1", 5)]
    [InlineData("'a[b'!A1", 0)]       // an invalid quoted name: at the opening quote
    [InlineData("1+'My Sheet!A1", 2)] // unterminated quote: at the opening quote
    [InlineData("'Data' !A1", 6)]     // a quoted name not followed by '!': at the character after the quote
    public void PrefixErrors_HaveStablePositions(string formula, int position)
        => Assert.Equal(position, Parse(formula).ErrorPosition);

    [Fact]
    public void QualifiedRange_ErrorPointsAtTheSecondPrefix()
    {
        var parsed = Parse("Sheet2!A1:Sheet2!B3");
        Assert.Equal(10, parsed.ErrorPosition);
    }

    // ---- Printer ----

    [Theory]
    [InlineData("=Data!B2*2", "=Data!B2*2")]
    [InlineData("='My Sheet'!A1:B3", "='My Sheet'!A1:B3")]
    [InlineData("='O''Brien'!$A$1", "='O''Brien'!$A$1")]
    [InlineData("='Data'!A1", "=Data!A1")]                 // a redundantly quoted name prints unquoted
    [InlineData("= Data!b$2 : $c3 ", "=Data!B$2:$C3")]
    [InlineData("='2024'!A1", "='2024'!A1")]
    [InlineData("='true'!A1", "='true'!A1")]
    [InlineData("=SUM(x.y!A1:A2,'a b'!C1)&Data!A1", "=SUM(x.y!A1:A2,'a b'!C1)&Data!A1")]
    public void RoundTrips_WithoutResolver(string input, string expected)
    {
        string printed = P(input);
        Assert.Equal(expected, printed);
        Assert.Equal(printed, P(printed)); // a fixed point
    }

    [Fact]
    public void Resolver_SuppliesTheDefinedCasing()
    {
        var sheets = new Sheets("Data", "My Sheet", "O'Brien");
        Assert.Equal("=Data!A1", P("=data!a1", sheets: sheets));
        Assert.Equal("='My Sheet'!A1:B3", P("='MY SHEET'!a1:b3", sheets: sheets));
        Assert.Equal("='O''Brien'!$A$1", P("='o''brien'!$a$1", sheets: sheets));
        Assert.Equal("=Data!A1+DATA!A1", P("=Data!A1+DATA!A1"));                          // no resolver: as written
        Assert.Equal("=Other!A1+'my data'!B2", P("=Other!A1+'my data'!B2", sheets: sheets)); // unknown: as written
    }

    [Fact]
    public void QualifiedReference_StaysQualified()
        => Assert.Equal("=Sheet1!A1+A1", P("=Sheet1!A1+A1", sheets: new Sheets("Sheet1")));

    [Fact]
    public void Resolver_ReturningADifferentSheet_Throws()
    {
        var root = Root("=Data!A1");
        Assert.Throws<InvalidOperationException>(() => FormulaPrinter.Print(root, null, null, new WrongSheets()));
    }

    private sealed class WrongSheets : ISheetCasingResolver
    {
        public string? GetDefinedCasing(string sheet) => "Other";
    }

    // ---- Rewriter sees and replaces the sheet ----

    /// <summary>A RenameSheet / DeleteSheet style rewriter: renames one sheet, turns another into #REF!.</summary>
    private sealed class SheetRewriter(string rename, string to, string delete) : IReferenceRewriter
    {
        public List<string?> Seen { get; } = [];

        public CellReference? Rewrite(CellReference reference) => throw new InvalidOperationException("not called");

        public (string? Sheet, CellReference Reference)? Rewrite(string? sheet, CellReference reference)
        {
            Seen.Add(sheet);
            return Map(sheet) is var (keep, s) && keep ? (s, reference) : null;
        }

        public (string? Sheet, CellReference Start, CellReference End)? RewriteRange(string? sheet, CellReference start, CellReference end)
        {
            Seen.Add(sheet);
            return Map(sheet) is var (keep, s) && keep ? (s, start, end) : null;
        }

        private (bool Keep, string? Sheet) Map(string? sheet)
        {
            if (sheet is null) return (true, null);
            if (sheet.Equals(delete, StringComparison.OrdinalIgnoreCase)) return (false, null);
            return (true, sheet.Equals(rename, StringComparison.OrdinalIgnoreCase) ? to : sheet);
        }
    }

    [Fact]
    public void SheetAwareRewriter_RenamesAndInvalidatesQualifiedReferences()
    {
        var rw = new SheetRewriter("Data", "My Data", "Gone");
        Assert.Equal("='My Data'!A1+A2+#REF!+SUM('My Data'!B1:B3,#REF!,C1:C2)",
            P("=data!A1+A2+Gone!A3+SUM(DATA!B1:B3,'gone'!B1:B3,C1:C2)", rw));
        Assert.Equal(new string?[] { "data", null, "Gone", "DATA", "gone", null }, rw.Seen);
    }

    /// <summary>Qualifies every unqualified reference, the way a rewriter may add a sheet.</summary>
    private sealed class Qualify(string sheet) : IReferenceRewriter
    {
        public CellReference? Rewrite(CellReference reference) => reference;
        public (string? Sheet, CellReference Reference)? Rewrite(string? s, CellReference reference) => (s ?? sheet, reference);
        public (string? Sheet, CellReference Start, CellReference End)? RewriteRange(string? s, CellReference start, CellReference end)
            => (s ?? sheet, start, end);
    }

    [Fact]
    public void RewriterReturnedSheet_IsQuotedAndCasedLikeAnyOther()
    {
        Assert.Equal("='2024'!A1+'2024'!B1:C2", P("=A1+B1:C2", new Qualify("2024")));
        Assert.Equal("=Data!A1", P("=A1", new Qualify("data"), new Sheets("Data")));
    }

    private sealed class BadSheet : IReferenceRewriter
    {
        public CellReference? Rewrite(CellReference reference) => reference;
        public (string? Sheet, CellReference Reference)? Rewrite(string? s, CellReference reference) => ("a[b", reference);
    }

    [Fact]
    public void RewriterReturningAnInvalidSheet_Throws()
        => Assert.Throws<InvalidOperationException>(() => P("=A1", new BadSheet()));

    /// <summary>A sheet-unaware rewriter (only the Phase 3 members): the defaults keep each reference's sheet.</summary>
    private sealed class ShiftDown : IReferenceRewriter
    {
        public CellReference? Rewrite(CellReference r)
            => r.Address.Row == GridBounds.MaxRows ? null : r with { Address = new CellAddress(r.Address.Column, r.Address.Row + 1) };
    }

    [Fact]
    public void SheetUnawareRewriter_KeepsTheSheet()
    {
        Assert.Equal("=Data!A2+'My Sheet'!B2:C3+A2", P("=Data!A1+'My Sheet'!B1:C2+A1", new ShiftDown()));
        Assert.Equal("=#REF!+#REF!", P("=Data!A100000+'x y'!A1:A100000", new ShiftDown()));
    }

    // ---- Property: any valid sheet name, quoted when required or redundantly, round-trips ----

    private static readonly Gen<string> SheetGen =
        Gen.Choose(1, SheetName.MaxLength)
            .SelectMany(n => Gen.Elements('a', 'Z', '1', '_', '.', ' ', '\'', 'é', '!', '-', 'E').ArrayOf(n))
            .Select(cs => new string(cs))
            .Where(SheetName.IsValid); // drops names beginning or ending with '

    private static readonly Gen<string> PrefixGen =
        from name in SheetGen
        from redundant in Gen.Elements(false, true)
        select SheetName.RequiresQuoting(name) || redundant ? "'" + name.Replace("'", "''") + "'" : name;

    private static readonly Gen<string> CellGen =
        from colAbs in Gen.Elements("", "$")
        from col in Gen.Elements("a", "B", "zz")
        from rowAbs in Gen.Elements("", "$")
        from row in Gen.Elements("1", "99", "100000")
        select colAbs + col + rowAbs + row;

    private static readonly Gen<string> QualifiedGen =
        from prefix in PrefixGen
        from start in CellGen
        from end in Gen.OneOf(Gen.Constant(""), CellGen.Select(c => ":" + c))
        select prefix + "!" + start + end;

    private static readonly Gen<string> FormulaGen =
        from refs in Gen.OneOf(QualifiedGen, CellGen).ArrayOf().Where(a => a.Length > 0)
        from call in Gen.Elements(false, true)
        select call ? "SUM(" + string.Join(",", refs) + ")" : string.Join("+", refs);

    private static IEnumerable<(string? Sheet, string Ref)> References(FormulaNode root)
    {
        var stack = new Stack<FormulaNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case CellRefNode c: yield return (c.Sheet, c.Reference.ToString()); break;
                case RangeNode r: yield return (r.Sheet, r.Start + ":" + r.End); break;
                case BinaryNode b: stack.Push(b.Right); stack.Push(b.Left); break;
                case FunctionCallNode f: for (int i = f.Arguments.Count - 1; i >= 0; i--) stack.Push(f.Arguments[i]); break;
            }
        }
    }

    [Fact]
    public void PrintOfParse_ReParsesWithTheSameSheets_AndIsAFixedPoint()
    {
        Prop.ForAll(FormulaGen.ToArbitrary(), f =>
        {
            var first = FormulaParser.Parse(f, null);
            if (!first.Success) return false.Label($"generated formula did not parse: {f} ({first.ErrorMessage})");
            string p1 = FormulaPrinter.Print(first.Root!);
            var second = FormulaParser.Parse(p1[1..], null);
            if (!second.Success) return false.Label($"printed form did not parse: {p1} ({second.ErrorMessage})");
            if (!References(first.Root!).SequenceEqual(References(second.Root!))) return false.Label($"references changed: {f} -> {p1}");
            return (FormulaPrinter.Print(second.Root!) == p1).Label($"not idempotent: {p1}");
        }).QuickCheckThrowOnFailure();
    }

    // ---- No recursion (R10) ----

    [Fact]
    public void LongChainOfQualifiedReferences_ParsesAndPrintsOnASmallStack()
    {
        var sb = new StringBuilder("'My Sheet'!A1");
        for (int i = 2; i <= 2000; i++) sb.Append("+Data!A").Append(i).Append(":B").Append(i);
        string text = sb.ToString();
        string? printed = null;
        Evaluation.Eval.OnSmallStack(256 * 1024, () => printed = FormulaPrinter.Print(FormulaParser.Parse(text, null).Root!));
        Assert.Equal("=" + text, printed);
    }
}
