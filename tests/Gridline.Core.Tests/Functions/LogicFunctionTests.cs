using Gridline.Core.Evaluation;
using Gridline.Core.Tests.Evaluation;
using static Gridline.Core.Tests.Functions.Fx;

namespace Gridline.Core.Tests.Functions;

// Requirement IDs: §7.2 (logic and information), R13, R16, R18.
public class LogicFunctionTests
{
    private static readonly CellValue T = CellValue.FromBoolean(true);
    private static readonly CellValue F = CellValue.FromBoolean(false);

    // ---- IF ----

    [Fact]
    public void If_ChoosesBranch()
    {
        Assert.Equal(N(1), Run("IF(TRUE,1,2)"));
        Assert.Equal(N(2), Run("IF(FALSE,1,2)"));
        Assert.Equal(N(1), Run("IF(5,1,2)"));
        Assert.Equal(N(2), Run("IF(0,1,2)"));
        Assert.Equal(N(1), Run("IF(\"true\",1,2)"));
        Assert.Equal(N(2), Run("IF(\"FALSE\",1,2)"));
    }

    [Fact]
    public void If_OmittedBranches()
    {
        var cells = new DictionaryCells().Set("A1", true);
        Assert.Equal(N(3), Run("IF(A1,,3)", new DictionaryCells()));          // A1 blank -> false branch
        Assert.Equal(N(0), Run("IF(A1,,3)", cells));                            // omitted true -> 0
        Assert.Equal(F, Run("IF(FALSE,1)"));                                    // no false arg -> FALSE
        Assert.Equal(F, Run("IF(FALSE,1,)"));                                   // R38: omitted false is absent -> FALSE
        Assert.Equal(N(0), Run("IF(TRUE,,)"));                                  // omitted true -> 0
        Assert.Equal(F, Run("IF(FALSE,,)"));
        Assert.Equal(E(ErrorCodes.Value), Run("IF(,1,2)"));                     // R38: the condition has no default
        Assert.Equal(E(ErrorCodes.Value), Run("IF(,1)"));
    }

    [Fact]
    public void If_ConditionCoercion()
    {
        Assert.Equal(E(ErrorCodes.Value), Run("IF(\"abc\",1,2)"));
        Assert.Equal(E(ErrorCodes.Value), Run("IF(\"1\",1,2)"));
        Assert.Equal(N(2), Run("IF(A1,1,2)"));                                  // blank is FALSE
        Assert.Equal(E(ErrorCodes.NA), Run("IF(A1,1,2)", new DictionaryCells().Set("A1", E(ErrorCodes.NA))));
        Assert.Equal(E(ErrorCodes.Div0), Run("IF(1/0,1,2)"));
    }

    [Fact]
    public void If_IsLazy()
    {
        Assert.Equal(N(1), Run("IF(TRUE,1,1/0)"));
        Assert.Equal(N(2), Run("IF(FALSE,1/0,2)"));
        Assert.Equal(N(1), Run("IF(TRUE,1,NOSUCHFN(1))"));
        Assert.Equal(N(2), Run("IF(FALSE,NOSUCHFN(1),2)"));
        var cells = new DictionaryCells();
        Assert.Equal(N(1), Run("IF(TRUE,1,SUM(Z1:Z100))", cells));
        Assert.Equal(0, cells.Reads);
        // the taken branch error still shows
        Assert.Equal(E(ErrorCodes.Name), Run("IF(TRUE,NOSUCHFN(1),2)"));
    }

    // ---- AND / OR / XOR ----

    [Fact]
    public void And_DirectArguments()
    {
        Assert.Equal(T, Run("AND(TRUE,1,\"TRUE\")"));
        Assert.Equal(F, Run("AND(TRUE,0)"));
        Assert.Equal(E(ErrorCodes.Value), Run("AND(TRUE,\"abc\")"));
        Assert.Equal(E(ErrorCodes.Div0), Run("AND(TRUE,1/0)"));
    }

    [Fact]
    public void And_RangeIgnoresTextAndBlank()
    {
        var cells = new DictionaryCells().Set("A1", true).Set("A2", "text").Set("A4", 3);
        Assert.Equal(T, Run("AND(A1:A5)", cells));
        cells.Set("A5", 0);
        Assert.Equal(F, Run("AND(A1:A5)", cells));
        Assert.Equal(T, Run("OR(A1:A5)", cells));
    }

    [Fact]
    public void LogicalFunctions_NoLogicalValues_IsValueError()
    {
        var cells = new DictionaryCells().Set("A1", "x").Set("A2", "y");
        Assert.Equal(E(ErrorCodes.Value), Run("AND(A1:A3)", cells));
        Assert.Equal(E(ErrorCodes.Value), Run("OR(A1:A3)", cells));
        Assert.Equal(E(ErrorCodes.Value), Run("XOR(A1:A3)", cells));
        Assert.Equal(E(ErrorCodes.Value), Run("AND(B1)", cells));
    }

    [Fact]
    public void LogicalFunctions_RangeError_Propagates()
    {
        var cells = new DictionaryCells().Set("A1", true).Set("A2", E(ErrorCodes.NA)).Set("A3", E(ErrorCodes.Num));
        Assert.Equal(E(ErrorCodes.NA), Run("AND(A1:A3)", cells));
        Assert.Equal(E(ErrorCodes.NA), Run("OR(A1:A3)", cells));
        Assert.Equal(E(ErrorCodes.NA), Run("XOR(A1:A3)", cells));
    }

    [Fact]
    public void Or_Xor()
    {
        Assert.Equal(T, Run("OR(FALSE,0,1)"));
        Assert.Equal(F, Run("OR(FALSE,0)"));
        Assert.Equal(T, Run("XOR(TRUE,FALSE)"));
        Assert.Equal(F, Run("XOR(TRUE,TRUE)"));
        Assert.Equal(T, Run("XOR(TRUE,TRUE,TRUE)"));
        var cells = new DictionaryCells().Set("A1", true).Set("A2", 1).Set("A3", "t").Set("A4", false);
        Assert.Equal(F, Run("XOR(A1:A4)", cells));
    }

    // ---- NOT ----

    [Fact]
    public void Not_Coerces()
    {
        Assert.Equal(F, Run("NOT(TRUE)"));
        Assert.Equal(T, Run("NOT(0)"));
        Assert.Equal(T, Run("NOT(A1)"));                                       // blank
        Assert.Equal(E(ErrorCodes.Value), Run("NOT(\"x\")"));
        Assert.Equal(E(ErrorCodes.NA), Run("NOT(A1)", new DictionaryCells().Set("A1", E(ErrorCodes.NA))));
    }

    // ---- IFERROR / IFNA ----

    [Fact]
    public void IfError_CatchesAnyError()
    {
        Assert.Equal(N(9), Run("IFERROR(1/0,9)"));
        Assert.Equal(N(9), Run("IFERROR(NOSUCHFN(1),9)"));
        Assert.Equal(N(1), Run("IFERROR(1,9)"));
        Assert.Equal(N(9), Run("IFERROR(A1:A3,9)"));                           // R18 range -> #VALUE! caught
    }

    [Fact]
    public void IfError_AlternativeIsLazy()
    {
        Assert.Equal(N(1), Run("IFERROR(1,1/0)"));
        Assert.Equal(N(1), Run("IFERROR(1,NOSUCHFN(1))"));
        Assert.Equal(E(ErrorCodes.Div0), Run("IFERROR(1/0,1/0)"));
    }

    [Fact]
    public void IfNa_PassesOtherErrors()
    {
        var cells = new DictionaryCells().Set("A1", E(ErrorCodes.NA)).Set("A2", E(ErrorCodes.Div0));
        Assert.Equal(N(9), Run("IFNA(A1,9)", cells));
        Assert.Equal(E(ErrorCodes.Div0), Run("IFNA(A2,9)", cells));
        Assert.Equal(E(ErrorCodes.Div0), Run("IFNA(1/0,9)"));
        Assert.Equal(N(5), Run("IFNA(5,1/0)"));
        Assert.Equal(N(1), Run("IFNA(A1,1)", cells));
    }

    // ---- IS* ----

    [Fact]
    public void IsBlank_OnlyTrulyBlankReference()
    {
        var cells = new DictionaryCells().Set("A1", "").Set("A2", 0);
        Assert.Equal(T, Run("ISBLANK(B1)", cells));
        Assert.Equal(F, Run("ISBLANK(A1)", cells));                            // "" formula result is text
        Assert.Equal(F, Run("ISBLANK(A2)", cells));
        Assert.Equal(T, Run("ISBLANK((B1))", cells));
        Assert.Equal(F, Run("ISBLANK(\"\")", cells));                          // non-reference
        Assert.Equal(F, Run("ISBLANK(1)", cells));
        Assert.Equal(F, Run("ISBLANK(B1:B2)", cells));                         // range
    }

    [Fact]
    public void IsBlank_OnFormulaResultEmptyText()
    {
        // A1 holds a formula result of "": the provider returns Text "", never Blank.
        var cells = new DictionaryCells().Set("A1", CellValue.FromText(""));
        Assert.Equal(F, Run("ISBLANK(A1)", cells));
        Assert.Equal(T, Run("ISTEXT(A1)", cells));
    }

    [Fact]
    public void TypeTests()
    {
        var cells = new DictionaryCells().Set("A1", 1).Set("A2", "t").Set("A3", true)
            .Set("A4", E(ErrorCodes.NA)).Set("A5", E(ErrorCodes.Div0));
        Assert.Equal(T, Run("ISNUMBER(A1)", cells));
        Assert.Equal(F, Run("ISNUMBER(A2)", cells));
        Assert.Equal(F, Run("ISNUMBER(B9)", cells));
        Assert.Equal(T, Run("ISTEXT(A2)", cells));
        Assert.Equal(F, Run("ISTEXT(A1)", cells));
        Assert.Equal(T, Run("ISLOGICAL(A3)", cells));
        Assert.Equal(F, Run("ISLOGICAL(A1)", cells));
        Assert.Equal(T, Run("ISERROR(A4)", cells));
        Assert.Equal(T, Run("ISERROR(A5)", cells));
        Assert.Equal(F, Run("ISERROR(A1)", cells));
        Assert.Equal(T, Run("ISNA(A4)", cells));
        Assert.Equal(F, Run("ISNA(A5)", cells));
        Assert.Equal(T, Run("ISERROR(1/0)"));
        Assert.Equal(T, Run("ISNUMBER(1+1)"));
        Assert.Equal(T, Run("ISLOGICAL(TRUE)"));
        Assert.Equal(T, Run("ISTEXT(\"x\")"));
        Assert.Equal(T, Run("ISERROR(NOSUCHFN(1))"));
    }

    [Theory]
    [InlineData("ISNUMBER")]
    [InlineData("ISTEXT")]
    [InlineData("ISLOGICAL")]
    [InlineData("ISERROR")]
    [InlineData("ISNA")]
    [InlineData("ISBLANK")]
    public void TypeTests_MultiCellRange_IsFalse(string fn)
    {
        var cells = new DictionaryCells().Set("A1", 1).Set("A2", 2);
        Assert.Equal(F, Run($"{fn}(A1:A2)", cells));
    }

    [Fact]
    public void Arities()
    {
        Assert.Equal(E(ErrorCodes.Parse), Run("IF(1)"));
        Assert.Equal(E(ErrorCodes.Parse), Run("IF(1,2,3,4)"));
        Assert.Equal(E(ErrorCodes.Parse), Run("AND()"));
        Assert.Equal(E(ErrorCodes.Parse), Run("NOT(1,2)"));
        Assert.Equal(E(ErrorCodes.Parse), Run("IFERROR(1)"));
        Assert.Equal(E(ErrorCodes.Parse), Run("IFNA(1,2,3)"));
        Assert.Equal(E(ErrorCodes.Parse), Run("ISNA()"));
        Assert.Equal(T, Run("OR(" + string.Join(",", Enumerable.Repeat("TRUE", 255)) + ")"));
    }

    // Regression (§7.2, R11): a multi-cell range in scalar context is #VALUE!; errors order left to right with no short-circuit.
    [Fact]
    public void RangeConditionIsValue()
    {
        var cells = new DictionaryCells().Set("A1", true).Set("A2", true);
        Assert.Equal(E(ErrorCodes.Value), Run("IF(A1:A2,1,2)", cells));
        Assert.Equal(E(ErrorCodes.Value), Run("NOT(A1:A2)", cells));
    }

    [Fact]
    public void OrTrueThenErrorCell_PropagatesError()
    {
        var cells = new DictionaryCells().Set("A1", E(ErrorCodes.Ref));
        Assert.Equal(E(ErrorCodes.Ref), Run("OR(TRUE,A1)", cells));
        Assert.Equal(E(ErrorCodes.Ref), Run("OR(A1,TRUE)", cells));
        Assert.Equal(E(ErrorCodes.NA), Run("OR(#N/A,A1)", cells));
    }
}
