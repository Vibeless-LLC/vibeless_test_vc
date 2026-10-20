using Gridline.Core.Tests.Evaluation;
using static Gridline.Core.Tests.Functions.Fx;

namespace Gridline.Core.Tests.Functions;

// Requirement IDs: §7.5 (SUMIF, COUNTIF, AVERAGEIF), §7.0, §5.4 (arity), §6.3 (ordering), §4.3 rule 5, D-41, D-45, R38, R66, R68.
public class ConditionalFunctionTests
{
    private static readonly CellValue Val = E(ErrorCodes.Value);
    private static readonly CellValue Div0 = E(ErrorCodes.Div0);

    /// <summary>A1:A10: 1, 5, 10, "apple", "Banana", TRUE, FALSE, blank, blank, "" (empty text).</summary>
    private static DictionaryCells Mixed() => new DictionaryCells()
        .Set("A1", 1).Set("A2", 5).Set("A3", 10).Set("A4", "apple").Set("A5", "Banana").Set("A6", true).Set("A7", false)
        .Set("A10", "");

    private static CellValue Count(string criteria, DictionaryCells? c = null) =>
        Run($"COUNTIF(A1:A10,{criteria})", c ?? Mixed());

    private static Workbook Book(params (string Address, string Input)[] cells) =>
        Workbook.FromCells(cells.Select(c => new KeyValuePair<string, string>(c.Address, c.Input)));

    [Theory]
    [InlineData("COUNTIF(A1:A3)")]
    [InlineData("COUNTIF(A1:A3,1,B1:B3)")]
    [InlineData("SUMIF(A1:A3)")]
    [InlineData("SUMIF(A1:A3,1,B1:B3,1)")]
    [InlineData("AVERAGEIF(A1:A3)")]
    [InlineData("AVERAGEIF(A1:A3,1,B1:B3,1)")]
    public void WrongArity_IsParseError(string formula) => Assert.Equal(E(ErrorCodes.Parse), Run(formula));

    // ---- one test per row of the §7.5 table ----

    [Theory]
    [InlineData("\">3\"", 2)]        // 5 and 10; text, booleans and blanks never match
    [InlineData("\">=5\"", 2)]
    [InlineData("\"<5\"", 1)]
    [InlineData("\"<=5\"", 2)]
    [InlineData("\"=5\"", 1)]
    [InlineData("\"5\"", 1)]
    [InlineData("\"<>5\"", 2)]       // numeric cells only: 1 and 10
    [InlineData("\"<0\"", 0)]        // blanks are not zeros
    public void NumberOperand_MatchesNumericCellsOnly(string criteria, double expected) =>
        Assert.Equal(N(expected), Count(criteria));

    [Theory]
    [InlineData("\"apple\"", 1)]
    [InlineData("\"=APPLE\"", 1)]    // case-insensitive
    [InlineData("\"a*\"", 1)]
    [InlineData("\"*A*\"", 2)]       // apple, Banana
    [InlineData("\"?????\"", 1)]     // apple; Banana has six letters
    [InlineData("\"*\"", 3)]         // every text cell (the empty text included), never numbers or blanks
    [InlineData("\"app\"", 0)]       // whole-string match
    public void TextOperand_EqualsWithWildcards(string criteria, double expected) =>
        Assert.Equal(N(expected), Count(criteria));

    [Fact]
    public void TextOperand_TildeEscapes()
    {
        var c = new DictionaryCells().Set("A1", "a*b").Set("A2", "aXb").Set("A3", "what?").Set("A4", "what!").Set("A5", "~x");
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,\"a~*b\")", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,\"what~?\")", c));
        Assert.Equal(N(2), Run("COUNTIF(A1:A5,\"what?\")", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,\"~~x\")", c));       // ~ escapes any next character, itself included
        Assert.Equal(N(4), Run("COUNTIF(A1:A5,\"<>a~*b\")", c));
    }

    [Fact]
    public void TextOperand_NotEqual_MatchesEverythingElse_BlanksAndNumbersIncluded()
    {
        Assert.Equal(N(9), Count("\"<>apple\""));    // all ten cells but apple
        Assert.Equal(N(8), Count("\"<>*a*\""));      // apple and Banana excluded
        Assert.Equal(N(10), Count("\"<>zzz\""));
    }

    [Fact]
    public void TextOperand_Ordering_TextCellsOnly()
    {
        Assert.Equal(N(3), Count("\"<c\""));         // apple, Banana and the empty text; no numbers or booleans
        Assert.Equal(N(2), Count("\"<b\""));         // "" and apple
        Assert.Equal(N(1), Count("\">=b\""));        // Banana
        Assert.Equal(N(2), Count("\">a\""));         // apple, Banana
    }

    [Fact]
    public void TextOperand_Ordering_CaseInsensitive()
    {
        var c = new DictionaryCells().Set("A1", "apple").Set("A2", "Banana").Set("A3", "cherry").Set("A4", "DATE").Set("A5", 7);
        Assert.Equal(N(2), Run("COUNTIF(A1:A5,\"<c\")", c));
        Assert.Equal(N(2), Run("COUNTIF(A1:A5,\"<=BANANA\")", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,\">cherry\")", c));
        Assert.Equal(N(2), Run("COUNTIF(A1:A5,\">=CHERRY\")", c));
        Assert.Equal(N(2), Run("COUNTIF(A1:A5,\">banana\")", c));
    }

    [Fact]
    public void EmptyOperand_MatchesBlankAndEmptyText()
    {
        Assert.Equal(N(3), Count("\"\""));            // A8, A9 and the empty text in A10
        Assert.Equal(N(3), Count("\"=\""));
        Assert.Equal(N(3), Run("COUNTIF(A1:A10,B1)", Mixed()));   // a blank criteria cell behaves as ""
    }

    [Fact]
    public void NotEmptyAlone_MatchesNonBlankCells()
    {
        Assert.Equal(N(7), Count("\"<>\""));
        var c = new DictionaryCells().Set("A1", 0).Set("A2", "x").Set("A3", false).Set("A4", E(ErrorCodes.Div0));
        Assert.Equal(N(3), Run("COUNTIF(A1:A5,\"<>\")", c));      // the error cell never matches, A5 is blank
    }

    [Fact]
    public void NumberGrammar_PercentExponentSignAndDecimal()
    {
        var c = new DictionaryCells().Set("A1", 0.05).Set("A2", 1000).Set("A3", -2).Set("A4", 0.5).Set("A5", "5%");
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,\"5%\")", c));       // 0.05; the text cell "5%" is not numeric
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,\"1e3\")", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,\"=1E3\")", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,\"-2\")", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,\"+.5\")", c));
        Assert.Equal(N(3), Run("COUNTIF(A1:A5,\">0\")", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,\">=1e3\")", c));
        Assert.Equal(N(0), Run("COUNTIF(A1:A5,\"1e\")", c));       // not a number: text, so only text cells equal to "1e"
    }

    [Fact]
    public void BooleanOperand_MatchesBooleanCellsOnly_R68()
    {
        var c = new DictionaryCells().Set("A1", true).Set("A2", false).Set("A3", 1).Set("A4", 0).Set("A5", "TRUE").Set("A6", true);
        Assert.Equal(N(2), Run("COUNTIF(A1:A6,\"TRUE\")", c));
        Assert.Equal(N(2), Run("COUNTIF(A1:A6,\"=true\")", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A6,\"FALSE\")", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A6,\"<>TRUE\")", c));   // boolean cells only: the FALSE cell
        Assert.Equal(N(2), Run("COUNTIF(A1:A6,\">FALSE\")", c));   // FALSE < TRUE
        Assert.Equal(N(3), Run("COUNTIF(A1:A6,\">=FALSE\")", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A6,\"<TRUE\")", c));
        Assert.Equal(N(3), Run("COUNTIF(A1:A6,\"<=TRUE\")", c));
        Assert.Equal(N(0), Run("COUNTIF(A1:A6,\"<FALSE\")", c));
    }

    [Fact]
    public void CriteriaAsValue_MeansEquality()
    {
        var c = new DictionaryCells().Set("A1", 5).Set("A2", "5").Set("A3", true).Set("A4", 6).Set("A5", false);
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,5)", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,TRUE)", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,FALSE)", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,2+4)", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,A1)", c));           // a cell holding 5
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,A3)", c));           // a cell holding TRUE: booleans only
        Assert.Equal(N(1), Run("COUNTIF(A1:A5,A2)", c));           // a cell holding text "5": parsed as the number 5
    }

    [Fact]
    public void CriteriaFromCells_TextWithOperators_BlankAndError()
    {
        var c = new DictionaryCells().Set("A1", 1).Set("A2", 2).Set("A3", 3).Set("C1", ">1").Set("C3", E(ErrorCodes.Div0));
        Assert.Equal(N(2), Run("COUNTIF(A1:A3,C1)", c));
        Assert.Equal(N(0), Run("COUNTIF(A1:A3,C2)", c));           // blank criterion = "": no empty cells here
        Assert.Equal(Div0, Run("COUNTIF(A1:A3,C3)", c));           // an error criterion propagates
        Assert.Equal(Div0, Run("SUMIF(A1:A3,C3)", c));
        Assert.Equal(Div0, Run("AVERAGEIF(A1:A3,C3,A1:A3)", c));
        Assert.Equal(E(ErrorCodes.Name), Run("COUNTIF(A1:A3,NOSUCH(1))", c));
    }

    [Fact]
    public void ErrorCellsInRange_NeverMatch()
    {
        var c = new DictionaryCells().Set("A1", E(ErrorCodes.Div0)).Set("A2", 1).Set("A3", E(ErrorCodes.NA)).Set("A4", "x");
        Assert.Equal(N(2), Run("COUNTIF(A1:A4,\"<>zzz\")", c));    // "every cell that does not match" still excludes errors
        Assert.Equal(N(2), Run("COUNTIF(A1:A4,\"<>\")", c));
        Assert.Equal(N(0), Run("COUNTIF(A1:A4,\"#DIV/0!\")", c));
        Assert.Equal(N(1), Run("COUNTIF(A1:A4,\"*\")", c));        // only "x"
        var onlyErrors = new DictionaryCells().Set("A1", E(ErrorCodes.Div0));
        Assert.Equal(N(0), Run("COUNTIF(A1:A1,\"\")", onlyErrors));
        Assert.Equal(N(0), Run("COUNTIF(A1:A1,\"<>x\")", onlyErrors));
    }

    // ---- SUMIF / AVERAGEIF ----

    /// <summary>A: east, west, East, north. B: 10, 20, 30, "n/a". C: 1..4.</summary>
    private static DictionaryCells Sales() => new DictionaryCells()
        .Set("A1", "east").Set("A2", "west").Set("A3", "East").Set("A4", "north")
        .Set("B1", 10).Set("B2", 20).Set("B3", 30).Set("B4", "n/a")
        .Set("C1", 1).Set("C2", 2).Set("C3", 3).Set("C4", 4);

    [Fact]
    public void SumIf_WithAndWithoutSumRange()
    {
        var s = Sales();
        Assert.Equal(N(40), Run("SUMIF(A1:A4,\"east\",B1:B4)", s));
        Assert.Equal(N(4), Run("SUMIF(A1:A4,\"east\",C1:C4)", s));
        Assert.Equal(N(9), Run("SUMIF(C1:C4,\">1\")", s));         // sums range itself when sum_range is absent
        Assert.Equal(N(9), Run("SUMIF(C1:C4,\">1\",)", s));        // an omitted third argument is absent (R38)
        Assert.Equal(N(0), Run("SUMIF(A1:A4,\"zzz\",B1:B4)", s));
    }

    [Fact]
    public void SumIf_OnlyNumbersAreAggregated()
    {
        var s = Sales();
        Assert.Equal(N(0), Run("SUMIF(A1:A4,\"north\",B1:B4)", s));    // the matched position holds text "n/a"
        s.Set("B2", true);
        Assert.Equal(N(0), Run("SUMIF(A1:A4,\"west\",B1:B4)", s));     // booleans are not aggregated either
    }

    [Fact]
    public void SumIf_ErrorAtMatchedPositionPropagates_UnmatchedDoesNot()
    {
        var s = Sales().Set("B2", E(ErrorCodes.Div0)).Set("B4", E(ErrorCodes.NA));
        Assert.Equal(Div0, Run("SUMIF(A1:A4,\"west\",B1:B4)", s));
        Assert.Equal(N(40), Run("SUMIF(A1:A4,\"east\",B1:B4)", s));
        Assert.Equal(E(ErrorCodes.NA), Run("SUMIF(A1:A4,\"north\",B1:B4)", s));
        Assert.Equal(Div0, Run("SUMIF(A1:A4,\"<>east\",B1:B4)", s));   // west (row 2) is the first matched error
        Assert.Equal(Div0, Run("AVERAGEIF(A1:A4,\"west\",B1:B4)", s));
    }

    [Fact]
    public void SumRange_AnchoredAtTopLeft_AndResizedToRange()
    {
        var s = new DictionaryCells().Set("A1", 1).Set("A2", 1).Set("A3", 2)
            .Set("B1", 10).Set("B2", 20).Set("B3", 30).Set("B4", 40).Set("B5", 50);
        Assert.Equal(N(30), Run("SUMIF(A1:A3,1,B1:B1)", s));       // B1:B1 resized to B1:B3: 10 + 20
        Assert.Equal(N(30), Run("SUMIF(A1:A3,1,B1)", s));          // a single-cell anchor
        Assert.Equal(N(50), Run("SUMIF(A1:A3,2,B3)", s));          // B3:B5; the match is the third position, B5
        Assert.Equal(N(70), Run("SUMIF(A1:A3,1,B3)", s));          // B3 + B4
        Assert.Equal(N(30), Run("SUMIF(A1:A3,1,B1:B5)", s));       // a larger sum_range is cut to range's size
    }

    [Fact]
    public void SumRange_TwoDimensionalResize()
    {
        var s = new DictionaryCells().Set("A1", 1).Set("B1", 0).Set("A2", 0).Set("B2", 1)
            .Set("D1", 1).Set("E1", 2).Set("D2", 3).Set("E2", 4).Set("E3", 100).Set("F3", 200);
        Assert.Equal(N(5), Run("SUMIF(A1:B2,1,D1)", s));           // D1:E2, positions (1,1) and (2,2): 1 + 4
        Assert.Equal(N(204), Run("SUMIF(A1:B2,1,E2)", s));         // E2:F3: 4 + 200
    }

    [Fact]
    public void SumRange_PositionsBeyondTheGridAreIgnored()
    {
        var rows = new DictionaryCells().Set("A99999", 1).Set("A100000", 1).Set("B99999", 7).Set("B100000", 9);
        Assert.Equal(N(9), Run("SUMIF(A99999:A100000,1,B100000)", rows));   // row 100001 does not exist
        Assert.Equal(N(2), Run("COUNTIF(A99999:A100000,1)", rows));
        var cols = new DictionaryCells().Set("A1", 1).Set("B1", 1).Set("C1", 1).Set("ZZ1", 4).Set("ZY1", 3);
        Assert.Equal(N(7), Run("SUMIF(A1:C1,1,ZY1)", cols));                // ZY, ZZ, then column 703 is ignored
        Assert.Equal(N(4), Run("SUMIF(A1:C1,1,ZZ1)", cols));
        Assert.Equal(N(4), Run("AVERAGEIF(A1:C1,1,ZZ1)", cols));            // the divisor counts aggregated numbers only
    }

    [Fact]
    public void AverageIf_Basics_AndDivByZero()
    {
        var s = Sales();
        Assert.Equal(N(20), Run("AVERAGEIF(A1:A4,\"east\",B1:B4)", s));
        Assert.Equal(N(3), Run("AVERAGEIF(C1:C4,\">1\")", s));
        Assert.Equal(Div0, Run("AVERAGEIF(A1:A4,\"zzz\",B1:B4)", s));      // no matches
        Assert.Equal(Div0, Run("AVERAGEIF(A1:A4,\"north\",B1:B4)", s));    // a match, but no number to average
        Assert.Equal(N(2.5), Run("AVERAGEIF(A1:A4,\"<>zzz\",C1:C4)", s));
        var mixed = new DictionaryCells().Set("A1", 1).Set("A2", 1).Set("A3", 1).Set("B1", 10).Set("B2", "x").Set("B3", 20);
        Assert.Equal(N(15), Run("AVERAGEIF(A1:A3,1,B1:B3)", mixed));       // the text at a matched position is skipped
    }

    [Fact]
    public void CountIf_CountsAllMatches_RegardlessOfValues() =>
        Assert.Equal(N(2), Run("COUNTIF(A1:A4,\"east\")", Sales()));

    [Fact]
    public void SingleCellRange_IsAccepted() => Assert.Equal(N(1), Run("COUNTIF(A1,5)", new DictionaryCells().Set("A1", 5)));

    // ---- range arguments (D-41, R68) ----

    [Theory]
    [InlineData("COUNTIF(5,5)")]
    [InlineData("COUNTIF(\"a\",\"a\")")]
    [InlineData("COUNTIF(1+1,2)")]
    [InlineData("COUNTIF(1/0,2)")]
    [InlineData("SUMIF(5,5,A1:A2)")]
    [InlineData("SUMIF(A1:A2,1,5)")]
    [InlineData("SUMIF(A1:A2,1,\"x\")")]
    [InlineData("AVERAGEIF(5,5)")]
    [InlineData("AVERAGEIF(A1:A2,1,5)")]
    public void DirectNonReferenceRanges_AreValueErrors(string formula) => Assert.Equal(Val, Run(formula));

    [Fact]
    public void ArgumentsAreProcessedInOrder()
    {
        var c = new DictionaryCells().Set("C1", E(ErrorCodes.Div0));
        Assert.Equal(Val, Run("COUNTIF(5,C1)", c));                // the range check comes before the criteria error
        Assert.Equal(Div0, Run("SUMIF(A1:A2,C1,5)", c));           // the criteria error comes before the sum_range check
    }

    [Fact]
    public void ParenthesisedReferencesStayReferences() =>
        Assert.Equal(N(1), Run("COUNTIF((A1:A2),5)", new DictionaryCells().Set("A1", 5)));

    [Fact]
    public void Names_AsRangeAndSumRange()
    {
        var wb = Book(("A1", "x"), ("A2", "y"), ("A3", "x"), ("B1", "1"), ("B2", "2"), ("B3", "4"), ("D1", "x"));
        wb.DefineName("Keys", "$A$1:$A$3");
        wb.DefineName("Vals", "$B$1:$B$3");
        wb.DefineName("Top", "$B$1");
        wb.DefineName("Crit", "$D$1");
        void Check(string formula, CellValue expected)
        {
            wb.SetCell("F1", formula);
            Assert.Equal(expected, wb.GetValue("F1"));
        }
        Check("=COUNTIF(Keys,\"x\")", N(2));
        Check("=SUMIF(Keys,\"x\",Vals)", N(5));
        Check("=SUMIF(Keys,\"x\",Top)", N(5));        // a single-cell name anchors and is resized
        Check("=SUMIF(A1:A3,\"x\",Top)", N(5));
        Check("=SUMIF(Keys,\"x\",B1)", N(5));
        Check("=AVERAGEIF(Keys,\"y\",Vals)", N(2));
        Check("=COUNTIF(Keys,Crit)", N(2));            // a name as criteria is an ordinary value
        Check("=SUMIF(Keys,Crit,Vals)", N(5));
    }

    [Fact]
    public void Names_UndefinedAndDeleted_GiveTheirOwnErrors()
    {
        var wb = Book(("A1", "1"));
        wb.DefineName("Gone", "$A$1:$A$2");
        wb.DeleteRows(1, 2);                           // the whole target is deleted: #REF!
        foreach (var (name, expected) in new[] { ("Gone", E(ErrorCodes.Ref)), ("Nope", E(ErrorCodes.Name)) })
        {
            foreach (var f in new[] { $"=COUNTIF({name},1)", $"=SUMIF({name},1)", $"=SUMIF(A1:A2,1,{name})",
                         $"=AVERAGEIF({name},1,A1:A2)", $"=AVERAGEIF(A1:A2,1,{name})" })
            {
                wb.SetCell("F1", f);
                Assert.Equal(expected, wb.GetValue("F1"));
            }
        }
    }
}
