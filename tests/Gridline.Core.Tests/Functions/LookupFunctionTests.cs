using Gridline.Core.Evaluation;
using Gridline.Core.Tests.Evaluation;
using static Gridline.Core.Tests.Functions.Fx;

namespace Gridline.Core.Tests.Functions;

// Requirement IDs: §7.4 (lookup and reference), §7.0, §5.4 (arity), §6.3 (ordering), §6.4, §6.6, §6.9, D-41, R65, R66, R67.
public class LookupFunctionTests
{
    private static readonly CellValue NA = E(ErrorCodes.NA);
    private static readonly CellValue Val = E(ErrorCodes.Value);
    private static readonly CellValue Ref = E(ErrorCodes.Ref);
    private static CellValue T(string s) => CellValue.FromText(s);
    private static CellValue B(bool b) => CellValue.FromBoolean(b);

    /// <summary>A1:A4 = 1,3,5,7; B1:B4 = a,b,c,d; C1:C4 = 10,20,30,40; row 6 = 1,3,5,7 and row 7 = x,y,z,w.</summary>
    private static DictionaryCells Table() => new DictionaryCells()
        .Set("A1", 1).Set("A2", 3).Set("A3", 5).Set("A4", 7)
        .Set("B1", "a").Set("B2", "b").Set("B3", "c").Set("B4", "d")
        .Set("C1", 10).Set("C2", 20).Set("C3", 30).Set("C4", 40)
        .Set("A6", 1).Set("B6", 3).Set("C6", 5).Set("D6", 7)
        .Set("A7", "x").Set("B7", "y").Set("C7", "z").Set("D7", "w");

    private static Workbook Book(params (string Address, string Input)[] cells) =>
        Workbook.FromCells(cells.Select(c => new KeyValuePair<string, string>(c.Address, c.Input)));

    // ---- arity (§5.4) ----

    [Theory]
    [InlineData("VLOOKUP(1,A1:B2)")]
    [InlineData("VLOOKUP(1,A1:B2,1,TRUE,1)")]
    [InlineData("HLOOKUP(1,A1:B2)")]
    [InlineData("HLOOKUP(1,A1:B2,1,TRUE,1)")]
    [InlineData("INDEX(A1:A3)")]
    [InlineData("INDEX(A1:A3,1,1,1)")]
    [InlineData("MATCH(1)")]
    [InlineData("MATCH(1,A1:A3,1,1)")]
    [InlineData("CHOOSE(1)")]
    [InlineData("CHOOSE()")]
    [InlineData("ROW(A1,A2)")]
    [InlineData("COLUMN(A1,A2)")]
    [InlineData("ROWS()")]
    [InlineData("ROWS(A1,A2)")]
    [InlineData("COLUMNS()")]
    [InlineData("COLUMNS(A1,A2)")]
    public void WrongArity_IsParseError(string formula) => Assert.Equal(E(ErrorCodes.Parse), Run(formula));

    // ---- VLOOKUP ----

    [Fact]
    public void VLookup_Approximate_LastRowAtOrBelow()
    {
        var t = Table();
        Assert.Equal(N(10), Run("VLOOKUP(1,A1:C4,3)", t));
        Assert.Equal(N(20), Run("VLOOKUP(3,A1:C4,3)", t));
        Assert.Equal(N(20), Run("VLOOKUP(4,A1:C4,3)", t));
        Assert.Equal(N(40), Run("VLOOKUP(100,A1:C4,3)", t));
        Assert.Equal(T("c"), Run("VLOOKUP(5.5,A1:C4,2,TRUE)", t));
        Assert.Equal(NA, Run("VLOOKUP(0.5,A1:C4,3)", t));            // below the first key
    }

    [Fact]
    public void VLookup_Approximate_UsesSection63Ordering()
    {
        var t = new DictionaryCells().Set("A1", 1).Set("A2", 9).Set("A3", "b").Set("A4", "d").Set("A5", true)
            .Set("B1", "n1").Set("B2", "n9").Set("B3", "tb").Set("B4", "td").Set("B5", "bt");
        Assert.Equal(T("tb"), Run("VLOOKUP(\"B\",A1:B5,2)", t));      // text compares case-insensitively
        Assert.Equal(T("tb"), Run("VLOOKUP(\"c\",A1:B5,2)", t));
        Assert.Equal(T("n9"), Run("VLOOKUP(50,A1:B5,2)", t));         // numbers sort before text
        Assert.Equal(T("bt"), Run("VLOOKUP(TRUE,A1:B5,2)", t));       // booleans after text
        Assert.Equal(T("td"), Run("VLOOKUP(FALSE,A1:B5,2)", t));
    }

    [Fact]
    public void VLookup_Approximate_ManyRowsBinarySearch()
    {
        var t = new DictionaryCells();
        for (int i = 1; i <= 1000; i++) { t.Set($"A{i}", i * 2); t.Set($"B{i}", i); }
        for (int v = 2; v <= 2000; v += 7) Assert.Equal(N(v / 2), Run($"VLOOKUP({v},A1:B1000,2)", t));
        Assert.Equal(N(500), Run("VLOOKUP(1001,A1:B1000,2)", t));
        t.ResetReads();
        Assert.Equal(N(500), Run("VLOOKUP(1001,A1:B1000,2)", t));
        Assert.True(t.Reads <= 2 * 10 + 2, $"{t.Reads} reads");        // O(log n): about 10 probes plus the result cell
    }

    [Fact]
    public void Approximate_DuplicateKeys_PickTheLast()
    {
        var t = new DictionaryCells().Set("A1", 1).Set("A2", 3).Set("A3", 3).Set("A4", 5)
            .Set("B1", "p").Set("B2", "q").Set("B3", "r").Set("B4", "s");
        Assert.Equal(T("r"), Run("VLOOKUP(3,A1:B4,2)", t));
        Assert.Equal(N(3), Run("MATCH(3,A1:A4)", t));
        Assert.Equal(N(3), Run("MATCH(4,A1:A4,1)", t));
        var d = new DictionaryCells().Set("A1", 9).Set("A2", 5).Set("A3", 5).Set("A4", 1);
        Assert.Equal(N(3), Run("MATCH(5,A1:A4,-1)", d));
        Assert.Equal(N(3), Run("MATCH(4,A1:A4,-1)", d));
        Assert.Equal(N(2), Run("MATCH(5,A1:A4,0)", d));                 // exact still takes the first
    }

    [Fact]
    public void Approximate_BlankKeysAdaptLikeSection63()
    {
        // ValueComparer: a blank key compares as 0 against a number, "" against text, FALSE against a boolean.
        var t = new DictionaryCells().Set("A1", -5).Set("A3", 3).Set("B1", "r1").Set("B2", "r2").Set("B3", "r3");
        Assert.Equal(T("r2"), Run("VLOOKUP(1,A1:B3,2)", t));            // blank A2 acts as 0 <= 1
        Assert.Equal(T("r2"), Run("VLOOKUP(0,A1:B3,2)", t));
        Assert.Equal(T("r2"), Run("VLOOKUP(Z9,A1:B3,2)", t));           // blank lookup value is 0 (R66)
        Assert.Equal(T("r1"), Run("VLOOKUP(-1,A1:B3,2)", t));
        Assert.Equal(N(2), Run("MATCH(1,A1:A3)", t));
        Assert.Equal(N(2), Run("MATCH(Z9,A1:A3)", t));
        Assert.Equal(N(2), Run("MATCH(Z9,A1:A2,-1)", new DictionaryCells().Set("A1", 5).Set("A2", 0)));
        var txt = new DictionaryCells().Set("A1", "a").Set("A3", "c");
        Assert.Equal(N(2), Run("MATCH(\"\",A1:A3,1)", txt));            // the blank key acts as "" and equals "" ("a" is greater)
    }

    [Fact]
    public void VLookup_Exact_FirstMatch()
    {
        var t = Table();
        Assert.Equal(N(20), Run("VLOOKUP(3,A1:C4,3,FALSE)", t));
        Assert.Equal(NA, Run("VLOOKUP(4,A1:C4,3,FALSE)", t));
        Assert.Equal(N(30), Run("VLOOKUP(\"c\",B1:C4,2,FALSE)", t));
        Assert.Equal(N(30), Run("VLOOKUP(\"C\",B1:C4,2,0)", t));       // case-insensitive, 0 is FALSE
        Assert.Equal(NA, Run("VLOOKUP(\"e\",B1:C4,2,FALSE)", t));
        var dup = new DictionaryCells().Set("A1", "k").Set("B1", 1).Set("A2", "K").Set("B2", 2);
        Assert.Equal(N(1), Run("VLOOKUP(\"k\",A1:B2,2,FALSE)", dup));
    }

    [Fact]
    public void VLookup_Exact_Wildcards()
    {
        var t = new DictionaryCells().Set("A1", "apple").Set("A2", "Banana").Set("A3", "a*b").Set("A4", "what?")
            .Set("B1", 1).Set("B2", 2).Set("B3", 3).Set("B4", 4);
        Assert.Equal(N(1), Run("VLOOKUP(\"a*\",A1:B4,2,FALSE)", t));
        Assert.Equal(N(2), Run("VLOOKUP(\"b?nana\",A1:B4,2,FALSE)", t));
        Assert.Equal(N(2), Run("VLOOKUP(\"*NAN*\",A1:B4,2,FALSE)", t));
        Assert.Equal(NA, Run("VLOOKUP(\"pple\",A1:B4,2,FALSE)", t));  // whole-string match, not "contains"
        Assert.Equal(NA, Run("VLOOKUP(\"appl\",A1:B4,2,FALSE)", t));
        Assert.Equal(N(3), Run("VLOOKUP(\"a~*b\",A1:B4,2,FALSE)", t)); // ~ escapes the star
        Assert.Equal(N(4), Run("VLOOKUP(\"what~?\",A1:B4,2,FALSE)", t));
        Assert.Equal(N(4), Run("VLOOKUP(\"w~hat~?\",A1:B4,2,FALSE)", t)); // ~ escapes any next character (D-32)
        Assert.Equal(N(1), Run("VLOOKUP(\"*\",A1:B4,2,FALSE)", t));
    }

    [Fact]
    public void VLookup_Exact_TypeRules()
    {
        var t = new DictionaryCells().Set("A1", "1").Set("B1", "text1").Set("A2", 1).Set("B2", "num1")
            .Set("A3", true).Set("B3", "bool").Set("A5", "last").Set("B5", "x").Set("B4", "blankrow");
        Assert.Equal(T("num1"), Run("VLOOKUP(1,A1:B5,2,FALSE)", t));  // a number never matches text "1"
        Assert.Equal(T("text1"), Run("VLOOKUP(\"1\",A1:B5,2,FALSE)", t));
        Assert.Equal(T("bool"), Run("VLOOKUP(TRUE,A1:B5,2,FALSE)", t));
        Assert.Equal(NA, Run("VLOOKUP(FALSE,A1:B5,2,FALSE)", t));
    }

    [Fact]
    public void VLookup_Exact_BlankKeysNeverMatch_BlankValueIsZero()
    {
        var t = new DictionaryCells().Set("A2", 5).Set("B1", "blankkey").Set("B2", "five");
        Assert.Equal(NA, Run("VLOOKUP(Z9,A1:B2,2,FALSE)", t));         // Z9 blank -> 0; the blank A1 does not match
        Assert.Equal(NA, Run("VLOOKUP(\"\",A1:B2,2,FALSE)", t));       // text "" does not match a blank key
        var zero = new DictionaryCells().Set("A1", 0).Set("B1", "zero");
        Assert.Equal(T("zero"), Run("VLOOKUP(Z9,A1:B1,2,FALSE)", zero));
        Assert.Equal(T("zero"), Run("VLOOKUP(Z9,A1:B1,2)", zero));      // approximate too
    }

    [Fact]
    public void VLookup_ColumnIndexRules()
    {
        var t = Table();
        Assert.Equal(Val, Run("VLOOKUP(1,A1:C4,0)", t));
        Assert.Equal(Val, Run("VLOOKUP(1,A1:C4,-1)", t));
        Assert.Equal(Ref, Run("VLOOKUP(1,A1:C4,4)", t));
        Assert.Equal(N(10), Run("VLOOKUP(1,A1:C4,3.9)", t));           // truncated toward zero
        Assert.Equal(Val, Run("VLOOKUP(1,A1:C4,0.5)", t));             // 0.5 truncates to 0
        Assert.Equal(Val, Run("VLOOKUP(1,A1:C4,\"x\")", t));
        Assert.Equal(N(1), Run("VLOOKUP(1,A1:C4,\"1\")", t));          // numeric text coerces
        Assert.Equal(Val, Run("VLOOKUP(1,A1:C4,)", t));                // omitted col has no default (R38)
        Assert.Equal(Ref, Run("VLOOKUP(99,A1:C4,9)", t));              // col check precedes the search
        Assert.Equal(Ref, Run("VLOOKUP(0,A1:C4,9)", t));
    }

    [Fact]
    public void VLookup_ApproxArgument()
    {
        var t = Table();
        Assert.Equal(N(20), Run("VLOOKUP(4,A1:C4,3,)", t));            // omitted -> TRUE (R38)
        Assert.Equal(NA, Run("VLOOKUP(4,A1:C4,3,\"false\")", t));
        Assert.Equal(Val, Run("VLOOKUP(4,A1:C4,3,\"maybe\")", t));
        Assert.Equal(NA, Run("VLOOKUP(4,A1:C4,3,Z9)", t));             // blank -> FALSE
        Assert.Equal(NA, Run("VLOOKUP(4,A1:C4,3,FALSE)", t));
    }

    [Fact]
    public void VLookup_ResultIsValue_BlankTargetIsBlank()
    {
        var t = new DictionaryCells().Set("A1", 1);
        var wb = Book(("A1", "1"), ("D1", "=VLOOKUP(1,A1:B1,2)"));
        Assert.Equal(N(0), wb.GetValue("D1"));                         // §6.6 top-level blank is 0
        var wb2 = Book(("A1", "1"), ("D1", "=VLOOKUP(1,A1:B1,2)&\"x\""));
        Assert.Equal(T("x"), wb2.GetValue("D1"));                      // blank target as text is ""
    }

    [Fact]
    public void VLookup_ErrorsPropagateInArgumentOrder()
    {
        var t = Table();
        Assert.Equal(E(ErrorCodes.Div0), Run("VLOOKUP(1/0,A1:C4,\"x\")", t));
        Assert.Equal(E(ErrorCodes.Div0), Run("VLOOKUP(1,1/0,\"x\")", t));
        Assert.Equal(E(ErrorCodes.Div0), Run("VLOOKUP(1,A1:C4,1/0,\"x\")", t));
        Assert.Equal(E(ErrorCodes.Div0), Run("VLOOKUP(1,A1:C4,1,1/0)", t));
        Assert.Equal(E(ErrorCodes.Name), Run("VLOOKUP(NOSUCH(1),A1:C4,0)", t));
        var errKey = new DictionaryCells().Set("A1", E(ErrorCodes.Div0)).Set("B1", 1).Set("A2", 4).Set("B2", 2);
        Assert.Equal(NA, Run("VLOOKUP(1,A1:B2,2,FALSE)", errKey));      // an error key just never matches
        Assert.Equal(E(ErrorCodes.Num), Run("VLOOKUP(1,A1:B2,2,FALSE)", new DictionaryCells().Set("A1", 1).Set("B1", E(ErrorCodes.Num))));
        Assert.Equal(Val, Run("VLOOKUP(A1:A2,A1:B2,2)", t));            // a range as the lookup value
    }

    [Fact]
    public void VLookup_DirectScalarTableIsOneByOne()
    {
        Assert.Equal(N(5), Run("VLOOKUP(5,5,1)"));
        Assert.Equal(N(5), Run("VLOOKUP(7,5,1)"));
        Assert.Equal(NA, Run("VLOOKUP(1,5,1)"));
        Assert.Equal(Ref, Run("VLOOKUP(5,5,2)"));
        Assert.Equal(T("x"), Run("VLOOKUP(\"x\",\"x\",1,FALSE)"));
        Assert.Equal(Val, Run("VLOOKUP(5,5,0)"));
    }

    // ---- HLOOKUP ----

    [Fact]
    public void HLookup_ApproximateAndExact()
    {
        var t = Table();
        Assert.Equal(T("y"), Run("HLOOKUP(3,A6:D7,2)", t));
        Assert.Equal(T("y"), Run("HLOOKUP(4,A6:D7,2)", t));
        Assert.Equal(T("w"), Run("HLOOKUP(99,A6:D7,2)", t));
        Assert.Equal(NA, Run("HLOOKUP(0,A6:D7,2)", t));
        Assert.Equal(N(5), Run("HLOOKUP(5,A6:D7,1,FALSE)", t));
        Assert.Equal(NA, Run("HLOOKUP(4,A6:D7,2,FALSE)", t));
        Assert.Equal(T("z"), Run("HLOOKUP(\"Z\",A7:D7,1,FALSE)", t));
        Assert.Equal(T("w"), Run("HLOOKUP(\"w*\",A7:D7,1,FALSE)", t));
        Assert.Equal(Val, Run("HLOOKUP(1,A6:D7,0)", t));
        Assert.Equal(Ref, Run("HLOOKUP(1,A6:D7,3)", t));
        Assert.Equal(N(1), Run("HLOOKUP(1,A6:D7,1.7)", t));
        Assert.Equal(NA, Run("HLOOKUP(Z9,A6:D6,1,FALSE)", new DictionaryCells().Set("D6", 7)));
    }

    // ---- INDEX ----

    [Fact]
    public void Index_SingleRowOrColumn_OneIndex()
    {
        var t = Table();
        Assert.Equal(N(5), Run("INDEX(A1:A4,3)", t));
        Assert.Equal(N(5), Run("INDEX(A6:D6,3)", t));
        Assert.Equal(N(1), Run("INDEX(A1:A4,1)", t));
        Assert.Equal(N(7), Run("INDEX(A1:A4,4)", t));
        Assert.Equal(Ref, Run("INDEX(A1:A4,5)", t));
        Assert.Equal(Ref, Run("INDEX(A6:D6,5)", t));
        Assert.Equal(N(1), Run("INDEX(A1,1)", t));
        Assert.Equal(Ref, Run("INDEX(A1,2)", t));
        Assert.Equal(N(5), Run("INDEX(A1:A4,3,1)", t));
        Assert.Equal(N(5), Run("INDEX(A6:D6,1,3)", t));
        Assert.Equal(Ref, Run("INDEX(A1:A4,3,2)", t));
        Assert.Equal(Ref, Run("INDEX(A6:D6,2,1)", t));
    }

    [Fact]
    public void Index_TwoDimensional()
    {
        var t = Table();
        Assert.Equal(N(20), Run("INDEX(A1:C4,2,3)", t));
        Assert.Equal(T("d"), Run("INDEX(A1:C4,4,2)", t));
        Assert.Equal(Ref, Run("INDEX(A1:C4,5,1)", t));
        Assert.Equal(Ref, Run("INDEX(A1:C4,1,4)", t));
        Assert.Equal(Val, Run("INDEX(A1:C4,2)", t));                   // 2-D with c omitted
        Assert.Equal(Val, Run("INDEX(A1:C4,2,)", t));                  // an omitted c is absent (R38)
        Assert.Equal(N(20), Run("INDEX(A1:C4,2.9,3.9)", t));           // truncated
    }

    [Fact]
    public void Index_ZeroNegativeAndBoundaries()
    {
        var t = Table();
        Assert.Equal(Val, Run("INDEX(A1:A4,0)", t));
        Assert.Equal(Val, Run("INDEX(A1:C4,0,1)", t));
        Assert.Equal(Val, Run("INDEX(A1:C4,1,0)", t));
        Assert.Equal(Val, Run("INDEX(A1:A4,-1)", t));                  // R65
        Assert.Equal(Val, Run("INDEX(A1:C4,1,-1)", t));
        Assert.Equal(Val, Run("INDEX(A1:C4,-1,9)", t));                // #VALUE! beats #REF!
        Assert.Equal(Val, Run("INDEX(A1:C4,0,9)", t));
        Assert.Equal(Val, Run("INDEX(A1:A4,0.5)", t));                 // truncates to 0
        Assert.Equal(Val, Run("INDEX(A1:A4,\"x\")", t));
        Assert.Equal(N(3), Run("INDEX(A1:A4,\"2\")", t));
        Assert.Equal(Val, Run("INDEX(A1:A4,)", t));                    // omitted r has no default
        Assert.Equal(Ref, Run("INDEX(A1:A4,1E10)", t));
    }

    [Fact]
    public void Index_ReturnsValues_BlankAndErrors()
    {
        var t = new DictionaryCells().Set("A1", 1).Set("A3", E(ErrorCodes.Div0));
        Assert.Equal(T("x"), Run("INDEX(A1:A3,2)&\"x\"", t));
        Assert.Equal(E(ErrorCodes.Div0), Run("INDEX(A1:A3,3)", t));
        Assert.Equal(N(2), Run("INDEX(A1:A3,1)+1", t));                // a value, usable in arithmetic
        Assert.Equal(N(0), Run("INDEX(A1:A3,2)", t));                  // §6.6
    }

    [Fact]
    public void Index_DirectScalarIsOneByOne()
    {
        Assert.Equal(N(5), Run("INDEX(5,1)"));
        Assert.Equal(N(5), Run("INDEX(5,1,1)"));
        Assert.Equal(Ref, Run("INDEX(5,2)"));
        Assert.Equal(Ref, Run("INDEX(5,1,2)"));
        Assert.Equal(T("s"), Run("INDEX(\"s\",1)"));
        Assert.Equal(E(ErrorCodes.Div0), Run("INDEX(1/0,1)"));
        Assert.Equal(E(ErrorCodes.Div0), Run("INDEX(A1:A2,1/0)", Table()));
        Assert.Equal(E(ErrorCodes.Div0), Run("INDEX(A1:A2,1,1/0)", Table()));
    }

    // ---- MATCH ----

    [Fact]
    public void Match_AscendingDefault()
    {
        var t = Table();
        Assert.Equal(N(2), Run("MATCH(3,A1:A4)", t));
        Assert.Equal(N(2), Run("MATCH(4,A1:A4,1)", t));
        Assert.Equal(N(4), Run("MATCH(100,A1:A4)", t));
        Assert.Equal(NA, Run("MATCH(0,A1:A4)", t));
        Assert.Equal(N(3), Run("MATCH(5,A6:D6)", t));                  // a single row
        Assert.Equal(N(2), Run("MATCH(4,A1:A4,)", t));                 // omitted type -> 1
        Assert.Equal(N(2), Run("MATCH(\"B\",B1:B4)", t));
    }

    [Fact]
    public void Match_TypeTruncatedAndSigned()
    {
        var t = Table();
        Assert.Equal(N(2), Run("MATCH(4,A1:A4,5)", t));                // any positive acts as 1
        Assert.Equal(NA, Run("MATCH(4,A1:A4,0.9)", t));
        Assert.Equal(N(2), Run("MATCH(3,A1:A4,0.9)", t));
        Assert.Equal(NA, Run("MATCH(4,A1:A4,-0.5)", t));               // -0.5 truncates to 0
        Assert.Equal(Val, Run("MATCH(4,A1:A4,\"x\")", t));
        Assert.Equal(N(2), Run("MATCH(4,A1:A4,\"1\")", t));
    }

    [Fact]
    public void Match_Descending()
    {
        var t = new DictionaryCells().Set("A1", 9).Set("A2", 7).Set("A3", 5).Set("A4", 3).Set("A5", 1);
        Assert.Equal(N(3), Run("MATCH(4,A1:A5,-1)", t));               // smallest value >= 4 is 5 at position 3
        Assert.Equal(N(4), Run("MATCH(3,A1:A5,-1)", t));
        Assert.Equal(N(3), Run("MATCH(4,A1:A5,-7)", t));               // any negative acts as -1
        Assert.Equal(N(1), Run("MATCH(9,A1:A5,-1)", t));
        Assert.Equal(NA, Run("MATCH(10,A1:A5,-1)", t));
        Assert.Equal(N(5), Run("MATCH(0,A1:A5,-1)", t));
    }

    [Fact]
    public void Match_Descending_BlankKeysAdaptLikeSection63()
    {
        // A blank key compares as 0 against a number (§6.3, R66), so 9, 5, blank, -2 is descending.
        var t = new DictionaryCells().Set("A1", 9).Set("A2", 5).Set("A4", -2);
        Assert.Equal(N(2), Run("MATCH(3,A1:A4,-1)", t));               // smallest value >= 3 is 5
        Assert.Equal(N(3), Run("MATCH(0,A1:A4,-1)", t));               // the blank A3 acts as 0
        Assert.Equal(N(3), Run("MATCH(-1,A1:A4,-1)", t));              // 0 >= -1
        Assert.Equal(N(3), Run("MATCH(Z9,A1:A4,-1)", t));              // a blank lookup value is 0 (R66)
        Assert.Equal(N(4), Run("MATCH(-2,A1:A4,-1)", t));
        Assert.Equal(NA, Run("MATCH(10,A1:A4,-1)", t));
        var txt = new DictionaryCells().Set("A1", "c");                // "c", blank: the blank acts as "" (descending)
        Assert.Equal(N(2), Run("MATCH(\"\",A1:A2,-1)", txt));
        Assert.Equal(N(1), Run("MATCH(\"b\",A1:A2,-1)", txt));
    }

    [Fact]
    public void Match_Exact()
    {
        var t = Table();
        Assert.Equal(N(3), Run("MATCH(5,A1:A4,0)", t));
        Assert.Equal(NA, Run("MATCH(4,A1:A4,0)", t));
        Assert.Equal(N(3), Run("MATCH(\"C\",B1:B4,0)", t));
        Assert.Equal(N(1), Run("MATCH(\"?\",B1:B4,0)", t));
        Assert.Equal(N(4), Run("MATCH(\"d*\",B1:B4,0)", t));
        Assert.Equal(N(2), Run("MATCH(\"y\",A7:D7,0)", t));
        var s = new DictionaryCells().Set("A1", "a*").Set("A2", "a~").Set("A3", "ab");
        Assert.Equal(N(1), Run("MATCH(\"a~*\",A1:A3,0)", s));
        Assert.Equal(N(1), Run("MATCH(\"a*\",A1:A3,0)", s));
        Assert.Equal(N(2), Run("MATCH(\"a~\",A1:A3,0)", s));           // trailing lone ~ is itself
        Assert.Equal(N(1), Run("MATCH(\"A?\",A3:A3,0)", s));
        Assert.Equal(NA, Run("MATCH(Z9,A1:A3,0)", s));                 // blank value -> 0, blank keys never match
        Assert.Equal(N(2), Run("MATCH(TRUE,A1:A3,0)", new DictionaryCells().Set("A1", 1).Set("A2", true)));
    }

    [Fact]
    public void Match_RangeShapeAndErrors()
    {
        var t = Table();
        Assert.Equal(NA, Run("MATCH(1,A1:B4)", t));                    // 2-D range -> #N/A
        Assert.Equal(NA, Run("MATCH(1,A1:B4,0)", t));
        Assert.Equal(N(1), Run("MATCH(5,5)"));                         // R65: a direct scalar is 1x1
        Assert.Equal(N(1), Run("MATCH(5,5,0)"));
        Assert.Equal(NA, Run("MATCH(4,5,0)"));
        Assert.Equal(E(ErrorCodes.Div0), Run("MATCH(1/0,A1:A4)", t));
        Assert.Equal(E(ErrorCodes.Div0), Run("MATCH(1,1/0)", t));
        Assert.Equal(E(ErrorCodes.Div0), Run("MATCH(1,A1:A4,1/0)", t));
        Assert.Equal(E(ErrorCodes.Div0), Run("MATCH(1/0,A1:B4)", t));  // errors precede the shape check
        Assert.Equal(NA, Run("MATCH(1,A1:A2,0)", new DictionaryCells().Set("A1", E(ErrorCodes.Div0))));
    }

    // ---- CHOOSE ----

    [Fact]
    public void Choose_PicksByIndex()
    {
        Assert.Equal(N(20), Run("CHOOSE(2,10,20,30)"));
        Assert.Equal(N(10), Run("CHOOSE(1,10,20,30)"));
        Assert.Equal(N(30), Run("CHOOSE(3.9,10,20,30)"));              // truncated
        Assert.Equal(N(20), Run("CHOOSE(\"2\",10,20,30)"));
        Assert.Equal(Val, Run("CHOOSE(0,10,20,30)"));
        Assert.Equal(Val, Run("CHOOSE(0.9,10,20,30)"));
        Assert.Equal(Val, Run("CHOOSE(-1,10,20,30)"));
        Assert.Equal(Val, Run("CHOOSE(4,10,20,30)"));
        Assert.Equal(Val, Run("CHOOSE(\"x\",10,20)"));
        Assert.Equal(Val, Run("CHOOSE(,10,20)"));
        Assert.Equal(E(ErrorCodes.Div0), Run("CHOOSE(1/0,10,20)"));
        Assert.Equal(T("b"), Run("CHOOSE(2,\"a\",\"b\")"));
    }

    [Fact]
    public void Choose_IsLazy()
    {
        Assert.Equal(N(1), Run("CHOOSE(1,1,1/0,NOSUCH(1))"));
        Assert.Equal(N(2), Run("CHOOSE(2,1/0,2,1/0)"));
        var cells = new DictionaryCells();
        Assert.Equal(N(1), Run("CHOOSE(1,1,SUM(Z1:Z100))", cells));
        Assert.Equal(0, cells.Reads);
        Assert.Equal(E(ErrorCodes.Div0), Run("CHOOSE(2,1,1/0)"));      // the chosen error surfaces
    }

    [Fact]
    public void Choose_ChosenArgumentLikeIfBranch()
    {
        var t = new DictionaryCells().Set("A1", 5).Set("A2", 6);
        Assert.Equal(N(5), Run("CHOOSE(2,0,A1)", t));
        Assert.Equal(Val, Run("CHOOSE(2,0,A1:A2)", t));                // a range, exactly as IF(TRUE,A1:A2)
        Assert.Equal(Run("IF(TRUE,A1:A2)", t), Run("CHOOSE(1,A1:A2,0)", t));
        Assert.Equal(Run("IF(TRUE,A1:A1)", t), Run("CHOOSE(1,A1:A1,0)", t));
        Assert.Equal(N(0), Run("CHOOSE(2,1,Z9)", t));                  // blank cell -> 0 at top level
        Assert.Equal(Val, Run("CHOOSE(2,1,)", t));                     // omitted chosen argument: no default, #VALUE! (R38)
        Assert.Equal(N(1), Run("CHOOSE(1,1,)", t));                    // an omitted argument not chosen is harmless
    }

    // ---- ROW / COLUMN / ROWS / COLUMNS ----

    [Fact]
    public void RowColumn_OfReferences()
    {
        Assert.Equal(N(5), Run("ROW(C5)"));
        Assert.Equal(N(3), Run("COLUMN(C5)"));
        Assert.Equal(N(2), Run("ROW(B2:D9)"));
        Assert.Equal(N(2), Run("COLUMN(B2:D9)"));
        Assert.Equal(N(2), Run("ROW(D9:B2)"));                         // top-left of the normalised range
        Assert.Equal(N(2), Run("COLUMN(D9:B2)"));
        Assert.Equal(N(7), Run("ROW($A$7)"));
        Assert.Equal(N(5), Run("ROW((C5))"));
        Assert.Equal(N(28), Run("COLUMN(AB1)"));
    }

    [Fact]
    public void RowColumn_DirectArgumentIsValueError()
    {
        Assert.Equal(Val, Run("ROW(5)"));
        Assert.Equal(Val, Run("COLUMN(\"A1\")"));
        Assert.Equal(Val, Run("ROW(A1+1)"));
        Assert.Equal(Val, Run("COLUMN(TRUE)"));
        Assert.Equal(Val, Run("ROW(1/0)"));                            // not a reference: #VALUE!, the argument is not evaluated
    }

    [Fact]
    public void RowColumn_NoArgument_NoOwningCell()
    {
        Assert.Equal(Val, Run("ROW()"));                               // evaluated outside any cell
        Assert.Equal(Val, Run("COLUMN()"));
    }

    [Fact]
    public void RowColumn_NoArgument_UseOwningCell()
    {
        var wb = Book(("C5", "=ROW()"), ("D7", "=COLUMN()"), ("AB10", "=ROW()*1000+COLUMN()"));
        Assert.Equal(N(5), wb.GetValue("C5"));
        Assert.Equal(N(4), wb.GetValue("D7"));
        Assert.Equal(N(10028), wb.GetValue("AB10"));
        wb.SetCell("B2", "=ROW()+COLUMN()");
        Assert.Equal(N(4), wb.GetValue("B2"));
        wb.SetCell("B2", "=A1");
        wb.SetCell("B2", "=ROW(C3)&COLUMN()");
        Assert.Equal(T("32"), wb.GetValue("B2"));
    }

    [Fact]
    public void RowsColumns_OnCellsRangesAndScalars()
    {
        var t = Table();
        Assert.Equal(N(1), Run("ROWS(A1)", t));
        Assert.Equal(N(1), Run("COLUMNS(A1)", t));
        Assert.Equal(N(4), Run("ROWS(A1:C4)", t));
        Assert.Equal(N(3), Run("COLUMNS(A1:C4)", t));
        Assert.Equal(N(4), Run("ROWS(C4:A1)", t));
        Assert.Equal(N(1), Run("ROWS(5)", t));                         // R65
        Assert.Equal(N(1), Run("COLUMNS(\"abc\")", t));
        Assert.Equal(N(100000), Run("ROWS(A1:A100000)"));
        Assert.Equal(0, t.Reads);                                      // dimensions need no cell reads
        Assert.Equal(E(ErrorCodes.Div0), Run("ROWS(1/0)"));
    }

    [Fact]
    public void ExactWildcards_DoNotBacktrackExponentially()
    {
        var t = new DictionaryCells().Set("A1", new string('a', 5000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(NA, Run("MATCH(\"*a*a*a*a*a*a*b\",A1:A1,0)", t));
        Assert.Equal(N(1), Run("MATCH(\"*a*a*a*a*a*a\",A1:A1,0)", t));
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    // ---- names (D-41) ----

    [Fact]
    public void Names_AreReferences()
    {
        var wb = Book(("A1", "1"), ("A2", "3"), ("A3", "5"), ("B1", "10"), ("B2", "20"), ("B3", "30"));
        wb.DefineName("Keys", "$A$1:$A$3");
        wb.DefineName("Tbl", "$A$1:$B$3");
        wb.DefineName("One", "$B$2");
        void Check(string formula, CellValue expected)
        {
            wb.SetCell("F1", formula);
            Assert.Equal(expected, wb.GetValue("F1"));
        }
        Check("=ROWS(Tbl)", N(3));
        Check("=COLUMNS(Tbl)", N(2));
        Check("=ROWS(One)", N(1));
        Check("=ROW(One)", N(2));
        Check("=COLUMN(Tbl)", N(1));
        Check("=ROW(Keys)", N(1));
        Check("=INDEX(Keys,2)", N(3));
        Check("=INDEX(Tbl,2,2)", N(20));
        Check("=MATCH(5,Keys)", N(3));
        Check("=MATCH(5,Keys,0)", N(3));
        Check("=VLOOKUP(3,Tbl,2,FALSE)", N(20));
        Check("=VLOOKUP(4,Tbl,2)", N(20));
        Check("=HLOOKUP(1,Tbl,1)", N(1));
        Check("=INDEX(One,1)", N(20));
    }

    [Fact]
    public void Names_UndefinedAndDeletedTargets_GiveTheirOwnErrors()
    {
        var wb = Book(("A1", "1"), ("A2", "3"), ("B1", "10"), ("B2", "20"));
        wb.DefineName("Gone", "$A$1:$B$2");
        wb.DeleteRows(1, 2);                                           // Gone's target is deleted -> #REF!
        void Check(string formula, string code)
        {
            wb.SetCell("F1", formula);
            Assert.Equal(E(code), wb.GetValue("F1"));
        }
        foreach (var (name, code) in new[] { ("Nope", ErrorCodes.Name), ("Gone", ErrorCodes.Ref) })
        {
            Check($"=INDEX({name},1)", code);
            Check($"=INDEX({name},1,1)", code);
            Check($"=MATCH(1,{name})", code);
            Check($"=MATCH(1,{name},0)", code);
            Check($"=VLOOKUP(1,{name},1)", code);
            Check($"=VLOOKUP(1,{name},1,FALSE)", code);
            Check($"=HLOOKUP(1,{name},1)", code);
            Check($"=ROWS({name})", code);
            Check($"=COLUMNS({name})", code);
            Check($"=ROW({name})", code);
            Check($"=COLUMN({name})", code);
            Check($"=ROW(({name}))", code);
        }
    }
}
