using static Gridline.Core.Tests.Functions.Fx;

namespace Gridline.Core.Tests.Functions;

// Requirement IDs: §5.3 (omitted argument), §6.4 (argument order), §7.1 (COUNT/COUNTA never error), §7.2 (IF defaults),
// §7.3 (LEFT/RIGHT [n=1], FIND/SEARCH [s=1], SUBSTITUTE [i]); controller ruling R38 (supersedes R34).
public class OmittedArgumentTests
{
    private static void Check(string formula, string expected) => Assert.Equal(expected, Run(formula).ToCanonicalString());

    // ---- Positions with a default: the default applies, exactly as if the argument were absent ----

    [Theory]
    [InlineData("IF(TRUE,,2)", "N:0")]                 // omitted t -> 0
    [InlineData("IF(FALSE,1,)", "B:FALSE")]            // omitted f -> FALSE (same as IF(FALSE,1))
    [InlineData("IF(FALSE,1)", "B:FALSE")]
    [InlineData("IF(TRUE,,)", "N:0")]
    [InlineData("IF(FALSE,,)", "B:FALSE")]
    [InlineData("LEFT(\"abc\",)", "T:a")]              // [n=1]
    [InlineData("RIGHT(\"abc\",)", "T:c")]
    [InlineData("FIND(\"b\",\"abcb\",)", "N:2")]       // [s=1]
    [InlineData("SEARCH(\"B\",\"abcb\",)", "N:2")]
    [InlineData("SUBSTITUTE(\"a-b-c\",\"-\",\"+\",)", "T:a+b+c")] // absent instance: every occurrence
    public void DefaultApplies(string formula, string expected) => Check(formula, expected);

    [Theory]
    [InlineData("LEFT(\"abc\",)", "LEFT(\"abc\")")]
    [InlineData("RIGHT(\"abc\",)", "RIGHT(\"abc\")")]
    [InlineData("FIND(\"c\",\"abc\",)", "FIND(\"c\",\"abc\")")]
    [InlineData("SEARCH(\"c\",\"abc\",)", "SEARCH(\"c\",\"abc\")")]
    [InlineData("SUBSTITUTE(\"aa\",\"a\",\"b\",)", "SUBSTITUTE(\"aa\",\"a\",\"b\")")]
    [InlineData("IF(FALSE,1,)", "IF(FALSE,1)")]
    public void OmittedEqualsAbsent(string omitted, string absent) =>
        Assert.Equal(Run(absent), Run(omitted));

    // ---- Positions with no default: #VALUE! ----

    [Theory]
    [InlineData("SUM(1,,2)")]
    [InlineData("AVERAGE(1,)")]
    [InlineData("MIN(1,)")]
    [InlineData("MAX(-1,)")]
    [InlineData("PRODUCT(2,,3)")]
    [InlineData("SUMSQ(2,)")]
    [InlineData("SUMPRODUCT(2,)")]
    [InlineData("AND(TRUE,)")]
    [InlineData("OR(TRUE,)")]
    [InlineData("XOR(TRUE,)")]
    [InlineData("CONCAT(\"a\",,\"b\")")]
    [InlineData("TEXTJOIN(\"-\",TRUE,\"a\",,\"b\")")]
    [InlineData("TEXTJOIN(,TRUE,\"a\")")]               // delimiter
    [InlineData("TEXTJOIN(\"-\",,\"a\")")]              // ignore_empty
    [InlineData("IF(,1,2)")]                           // the IF condition
    [InlineData("MID(\"abc\",,1)")]
    [InlineData("ROUND(1.5,)")]
    [InlineData("MOD(,2)")]
    [InlineData("EXACT(,\"\")")]
    [InlineData("REPLACE(\"abc\",1,1,)")]
    [InlineData("SUBSTITUTE(\"abc\",,\"x\")")]
    [InlineData("IFERROR(#N/A,)")]                     // the alternative has no default either
    public void NoDefault_IsValueError(string formula) => Check(formula, "E:#VALUE!");

    // NOT has arity 1, so `NOT()` has no argument at all (#PARSE!); its omitted case is reached through NOT(AND(TRUE,)).
    [Fact]
    public void Not_OfOmittedPropagatesValueError() => Check("NOT(AND(TRUE,))", "E:#VALUE!");

    // ---- §6.4: the omitted argument's #VALUE! takes its place in argument order ----

    [Theory]
    [InlineData("SUM(#N/A,,2)", "E:#N/A")]             // an earlier error wins
    [InlineData("SUM(1,,#N/A)", "E:#VALUE!")]          // the omitted #VALUE! comes first
    [InlineData("AND(#DIV/0!,)", "E:#DIV/0!")]
    [InlineData("CONCAT(\"a\",,#N/A)", "E:#VALUE!")]
    public void ValueErrorTakesItsPlaceInArgumentOrder(string formula, string expected) => Check(formula, expected);

    // ---- COUNT and COUNTA never error (§7.1): an omitted argument is simply ignored ----

    [Theory]
    [InlineData("COUNT(1,,2)", "N:2")]
    [InlineData("COUNTA(1,,2)", "N:2")]
    [InlineData("COUNT(,)", "N:0")]
    [InlineData("COUNTA(,)", "N:0")]
    [InlineData("COUNTA(,\"\")", "N:1")]
    public void CountAndCountA_IgnoreOmitted(string formula, string expected) => Check(formula, expected);

    // ---- IFERROR catches the omitted argument's #VALUE! ----

    [Theory]
    [InlineData("IFERROR(,5)", "N:5")]
    [InlineData("ISERROR(SUM(1,))", "B:TRUE")]
    [InlineData("IFNA(,5)", "E:#VALUE!")]              // IFNA catches only #N/A
    public void ErrorHandlersSeeValueError(string formula, string expected) => Check(formula, expected);
}
