using System.Diagnostics;
using Gridline.Core.Evaluation;
using Gridline.Core.Tests.Evaluation;
using static Gridline.Core.Tests.Functions.Fx;

namespace Gridline.Core.Tests.Functions;

// Requirement IDs: §7.3 (text), §6.2, §6.9, R14, R16, R19.
public class TextFunctionTests
{
    private static CellValue S(string s) => CellValue.FromText(s);
    private static CellValue V => E(ErrorCodes.Value);
    private static CellValue B(bool b) => CellValue.FromBoolean(b);
    private static DictionaryCells NaCell() => new DictionaryCells().Set("A9", E(ErrorCodes.NA));

    [Fact]
    public void Len()
    {
        Assert.Equal(N(5), Run("LEN(\"hello\")"));
        Assert.Equal(N(4), Run("LEN(12.5)"));
        Assert.Equal(N(0), Run("LEN(A1)"));
        Assert.Equal(N(4), Run("LEN(TRUE)"));
        Assert.Equal(N(2), Run("LEN(\"\U0001F600\")")); // surrogate pair = 2 UTF-16 units
        Assert.Equal(E(ErrorCodes.DivZero), Run("LEN(1/0)"));
        Assert.Equal(V, Run("LEN(A1:A2)"));
    }

    [Fact]
    public void LeftRight()
    {
        Assert.Equal(S("he"), Run("LEFT(\"hello\",2)"));
        Assert.Equal(S("h"), Run("LEFT(\"hello\")"));
        Assert.Equal(S("hello"), Run("LEFT(\"hello\",99)"));
        Assert.Equal(S(""), Run("LEFT(\"hello\",0)"));
        Assert.Equal(S("he"), Run("LEFT(\"hello\",2.9)"));
        Assert.Equal(V, Run("LEFT(\"hello\",-1)"));
        Assert.Equal(S("lo"), Run("RIGHT(\"hello\",2)"));
        Assert.Equal(S("o"), Run("RIGHT(\"hello\")"));
        Assert.Equal(S("hello"), Run("RIGHT(\"hello\",99)"));
        Assert.Equal(V, Run("RIGHT(\"hello\",-1)"));
        Assert.Equal(S("12"), Run("LEFT(123,2)"));
    }

    [Fact]
    public void Mid()
    {
        Assert.Equal(S("ell"), Run("MID(\"hello\",2,3)"));
        Assert.Equal(S("ello"), Run("MID(\"hello\",2,99)"));
        Assert.Equal(S(""), Run("MID(\"hello\",9,2)"));
        Assert.Equal(S(""), Run("MID(\"hello\",2,0)"));
        Assert.Equal(V, Run("MID(\"hello\",0,2)"));
        Assert.Equal(V, Run("MID(\"hello\",1,-1)"));
        Assert.Equal(S("el"), Run("MID(\"hello\",2.7,2.7)"));
    }

    [Fact]
    public void CaseFunctions()
    {
        Assert.Equal(S("ABC1"), Run("UPPER(\"abC1\")"));
        Assert.Equal(S("abc1"), Run("LOWER(\"ABc1\")"));
        Assert.Equal(S("O'Neil 2Nd"), Run("PROPER(\"o'neil 2nd\")"));
        Assert.Equal(S("Hello World"), Run("PROPER(\"hELLO wORLD\")"));
        Assert.Equal(S("A-B_C"), Run("PROPER(\"a-b_c\")"));
    }

    [Fact]
    public void Trim()
    {
        Assert.Equal(S("a b c"), Run("TRIM(\"  a   b  c  \")"));
        Assert.Equal(S("\ta"), Run("TRIM(\" \ta \")"));
        Assert.Equal(S(""), Run("TRIM(\"   \")"));
        Assert.Equal(S("a\t\tb"), Run("TRIM(\"a\t\tb\")"));
    }

    [Fact]
    public void Concat()
    {
        Assert.Equal(S("ab1TRUE"), Run("CONCAT(\"a\",\"b\",1,TRUE)"));
        var cells = new DictionaryCells().Set("A1", "x").Set("B1", 2).Set("A2", "y").Set("B2", "z");
        Assert.Equal(S("x2yz"), Run("CONCAT(A1:B2)", cells)); // row-major
        Assert.Equal(S("xy"), Run("CONCAT(A1,C9,A2)", cells)); // blank -> ""
        Assert.Equal(E(ErrorCodes.NA), Run("CONCAT(\"a\",A9,1/0)", NaCell()));
        Assert.Equal(E(ErrorCodes.DivZero), Run("CONCAT(1/0,A9)", NaCell()));
        Assert.Equal(E(ErrorCodes.NA), Run("CONCAT(A8:A9)", NaCell()));
        Assert.Equal(S("1E-05"), Run("CONCAT(0.00001)"));
    }

    [Fact]
    public void ConcatOverflow()
    {
        Assert.Equal(V, Run("CONCAT(REPT(\"x\",20000),REPT(\"y\",12768))"));
        Assert.Equal(N(32767), Run("LEN(CONCAT(REPT(\"x\",20000),REPT(\"y\",12767)))"));
    }

    [Fact]
    public void Textjoin()
    {
        Assert.Equal(S("a,b,c"), Run("TEXTJOIN(\",\",TRUE,\"a\",\"b\",\"c\")"));
        var cells = new DictionaryCells().Set("A1", "a").Set("C1", "c");
        Assert.Equal(S("a,c"), Run("TEXTJOIN(\",\",TRUE,A1:C1)", cells));
        Assert.Equal(S("a,,c"), Run("TEXTJOIN(\",\",FALSE,A1:C1)", cells));
        Assert.Equal(S("a,,c"), Run("TEXTJOIN(\",\",0,A1,\"\",C1)", cells));
        Assert.Equal(S("a,c"), Run("TEXTJOIN(\",\",1,A1,\"\",C1)", cells));
        Assert.Equal(S(""), Run("TEXTJOIN(\",\",TRUE,\"\")"));
        Assert.Equal(S("1--2"), Run("TEXTJOIN(\"--\",TRUE,1,2)"));
        Assert.Equal(V, Run("TEXTJOIN(\",\",\"x\",1)"));
        Assert.Equal(E(ErrorCodes.DivZero), Run("TEXTJOIN(\",\",TRUE,1/0)"));
        Assert.Equal(V, Run("TEXTJOIN(REPT(\"x\",20000),TRUE,1,2,3)"));
    }

    [Fact]
    public void Find()
    {
        Assert.Equal(N(3), Run("FIND(\"l\",\"hello\")"));
        Assert.Equal(N(4), Run("FIND(\"l\",\"hello\",4)"));
        Assert.Equal(V, Run("FIND(\"L\",\"hello\")"));       // case-sensitive
        Assert.Equal(V, Run("FIND(\"z\",\"hello\")"));
        Assert.Equal(N(2), Run("FIND(\"\",\"hello\",2)"));
        Assert.Equal(N(6), Run("FIND(\"\",\"hello\",6)"));
        Assert.Equal(V, Run("FIND(\"\",\"hello\",7)"));
        Assert.Equal(V, Run("FIND(\"l\",\"hello\",0)"));
        Assert.Equal(N(1), Run("FIND(\"*\",\"*a\")"));       // no wildcards
        Assert.Equal(N(3), Run("FIND(\"l\",\"hello\",2.9)"));
    }

    [Fact]
    public void Search()
    {
        Assert.Equal(N(3), Run("SEARCH(\"L\",\"hello\")"));
        Assert.Equal(N(1), Run("SEARCH(\"h*o\",\"HELLO\")"));
        Assert.Equal(N(2), Run("SEARCH(\"e?l\",\"hello\")"));
        Assert.Equal(V, Run("SEARCH(\"e?o\",\"hello\")"));
        Assert.Equal(N(3), Run("SEARCH(\"~*\",\"ab*c\")"));
        Assert.Equal(N(3), Run("SEARCH(\"~?\",\"ab?c\")"));
        Assert.Equal(V, Run("SEARCH(\"~?\",\"abxc\")"));
        Assert.Equal(N(2), Run("SEARCH(\"~~\",\"a~b\")"));
        Assert.Equal(N(3), Run("SEARCH(\"*\",\"abc\",3)"));
        Assert.Equal(N(2), Run("SEARCH(\"\",\"abc\",2)"));
        Assert.Equal(V, Run("SEARCH(\"\",\"abc\",5)"));
        Assert.Equal(N(2), Run("SEARCH(\"b*d\",\"abcabd\")"));
        Assert.Equal(N(5), Run("SEARCH(\"b*d\",\"abcabd\",3)"));
        Assert.Equal(N(1), Run("SEARCH(\"*c\",\"abc\")"));
        Assert.Equal(V, Run("SEARCH(\"*d\",\"abc\")"));
        Assert.Equal(V, Run("SEARCH(\"a\",\"abc\",0)"));
    }

    // Regression (§7.3): wildcard edge cases.
    [Fact]
    public void Search_WildcardRegression()
    {
        Assert.Equal(N(1), Run("SEARCH(\"a?c*d\",\"abcxacd\")"));
        Assert.Equal(N(3), Run("SEARCH(\"c~\",\"abc~\")"));          // dangling ~ stands for itself, at the end
        Assert.Equal(N(2), Run("SEARCH(\"~\",\"a~b\")"));
        Assert.Equal(V, Run("SEARCH(\"c~\",\"abc\")"));
        Assert.Equal(V, Run("SEARCH(\"*b\",\"abc\",3)"));
    }

    [Fact]
    public void Search_NearMissPathologicalPatternIsFast()
    {
        var sw = Stopwatch.StartNew();
        var r = Run("SEARCH(\"a*a*a*a*a*a*a*a*b\",REPT(\"a\",30000))");
        sw.Stop();
        Assert.Equal(V, r);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Search_PathologicalPatternsAreFast()
    {
        foreach (var formula in new[]
        {
            "SEARCH(\"*a*a*a*a*b\",REPT(\"a\",30000))",
            "SEARCH(\"a?a?a?a?a?b\",REPT(\"a\",30000))",
            "SEARCH(\"a*a*a*a*b\",REPT(\"a\",30000))",
        })
        {
            var sw = Stopwatch.StartNew();
            var r = Run(formula);
            sw.Stop();
            Assert.Equal(V, r);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"{formula} took {sw.ElapsedMilliseconds} ms");
        }
    }

    [Fact]
    public void Substitute()
    {
        Assert.Equal(S("axxd"), Run("SUBSTITUTE(\"abcd\",\"bc\",\"xx\")"));
        Assert.Equal(S("a-b-c"), Run("SUBSTITUTE(\"a b c\",\" \",\"-\")"));
        Assert.Equal(S("a-b c"), Run("SUBSTITUTE(\"a b c\",\" \",\"-\",1)"));
        Assert.Equal(S("a b-c"), Run("SUBSTITUTE(\"a b c\",\" \",\"-\",2)"));
        Assert.Equal(S("a b c"), Run("SUBSTITUTE(\"a b c\",\" \",\"-\",3)"));
        Assert.Equal(V, Run("SUBSTITUTE(\"a b c\",\" \",\"-\",0)"));
        Assert.Equal(V, Run("SUBSTITUTE(\"a b c\",\" \",\"-\",-1)"));
        Assert.Equal(S("abc"), Run("SUBSTITUTE(\"abc\",\"\",\"x\")"));
        Assert.Equal(S("abc"), Run("SUBSTITUTE(\"abc\",\"\",\"x\",1)"));
        Assert.Equal(S("Ab"), Run("SUBSTITUTE(\"ab\",\"a\",\"A\")"));
        Assert.Equal(S("ab"), Run("SUBSTITUTE(\"ab\",\"A\",\"x\")")); // case-sensitive
        Assert.Equal(S("bb"), Run("SUBSTITUTE(\"aaaa\",\"aa\",\"b\")")); // non-overlapping
        Assert.Equal(S("a"), Run("SUBSTITUTE(\"abc\",\"bc\",\"\")"));
        Assert.Equal(V, Run("SUBSTITUTE(REPT(\"a\",20000),\"a\",\"bb\")"));
    }

    [Fact]
    public void Replace()
    {
        Assert.Equal(S("aXXe"), Run("REPLACE(\"abcde\",2,3,\"XX\")"));
        Assert.Equal(S("abcdeX"), Run("REPLACE(\"abcde\",9,2,\"X\")"));
        Assert.Equal(S("Xabcde"), Run("REPLACE(\"abcde\",1,0,\"X\")"));
        Assert.Equal(S("aX"), Run("REPLACE(\"abcde\",2,99,\"X\")"));
        Assert.Equal(V, Run("REPLACE(\"abcde\",0,1,\"X\")"));
        Assert.Equal(V, Run("REPLACE(\"abcde\",1,-1,\"X\")"));
        Assert.Equal(S("aXcde"), Run("REPLACE(\"abcde\",2.9,1.9,\"X\")"));
    }

    [Fact]
    public void Rept()
    {
        Assert.Equal(S("ababab"), Run("REPT(\"ab\",3)"));
        Assert.Equal(S(""), Run("REPT(\"ab\",0)"));
        Assert.Equal(S("abab"), Run("REPT(\"ab\",2.9)"));
        Assert.Equal(V, Run("REPT(\"ab\",-1)"));
        Assert.Equal(N(32767), Run("LEN(REPT(\"x\",32767))"));
        Assert.Equal(V, Run("REPT(\"x\",32768)"));
        Assert.Equal(V, Run("REPT(\"x\",1000000000)"));
        Assert.Equal(V, Run("REPT(\"xx\",1E+300)"));
        Assert.Equal(S(""), Run("REPT(\"\",1000000000)"));
    }

    [Fact]
    public void Exact()
    {
        Assert.Equal(B(true), Run("EXACT(\"a\",\"a\")"));
        Assert.Equal(B(false), Run("EXACT(\"a\",\"A\")"));
        Assert.Equal(B(true), Run("EXACT(1,\"1\")"));
        Assert.Equal(B(true), Run("EXACT(A1,\"\")"));
        Assert.Equal(E(ErrorCodes.NA), Run("EXACT(A9,\"\")", NaCell()));
    }

    [Fact]
    public void Value()
    {
        Assert.Equal(N(12.5), Run("VALUE(\"12.5\")"));
        Assert.Equal(N(0.05), Run("VALUE(\"5%\")"));
        Assert.Equal(N(-3), Run("VALUE(\" -3 \")"));
        Assert.Equal(N(7), Run("VALUE(7)"));
        Assert.Equal(V, Run("VALUE(\"abc\")"));
        Assert.Equal(E(ErrorCodes.DivZero), Run("VALUE(1/0)"));
    }

    [Fact]
    public void ArityIsRegistered()
    {
        Assert.Equal(E(ErrorCodes.Parse), Run("LEN()"));
        Assert.Equal(E(ErrorCodes.Parse), Run("LEFT(\"a\",1,2)"));
        Assert.Equal(E(ErrorCodes.Parse), Run("TEXTJOIN(\",\",TRUE)"));
        Assert.Equal(E(ErrorCodes.Parse), Run("SUBSTITUTE(\"a\",\"b\")"));
    }

    [Fact]
    public void ErrorsPropagateInArgumentOrder()
    {
        Assert.Equal(E(ErrorCodes.NA), Run("MID(A9,1/0,1)", NaCell()));
        Assert.Equal(E(ErrorCodes.DivZero), Run("MID(\"a\",1/0,A9)", NaCell()));
    }
}
