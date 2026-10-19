using Gridline.Core.Addressing;
using Gridline.Core.Evaluation;
using static Gridline.Core.Tests.Evaluation.Eval;

namespace Gridline.Core.Tests.Evaluation;

// Requirement IDs: §6.8 / §7.0 (function-call interface: lazy arguments, reference vs direct), R12, §6.5.
public class FunctionArgumentsTests
{
    [Theory]
    [InlineData("ARGC()", 0)]
    [InlineData("ARGC(1)", 1)]
    [InlineData("ARGC(,)", 2)]
    [InlineData("ARGC(1,)", 2)]
    [InlineData("ARGC(1,,3)", 3)]
    public void Count_IncludesOmitted(string formula, double expected) => Assert.Equal(N(expected), Run(formula));

    [Theory]
    [InlineData("OMITMASK(1,,3)", 2)]
    [InlineData("OMITMASK(,)", 3)]
    [InlineData("OMITMASK(1,2)", 0)]
    [InlineData("OMITMASK(,\"\")", 1)]
    public void IsOmitted(string formula, double mask) => Assert.Equal(N(mask), Run(formula));

    [Fact]
    public void OmittedArgument_EvaluatesToBlank()
    {
        var fns = new FakeFunctions();
        fns.Add("KIND", a => CellValue.FromText(a.Evaluate(1).Kind.ToString()));
        Assert.Equal(T("Blank"), Run("KIND(1,)", functions: fns));
    }

    [Theory]
    [InlineData("ISREF(A1)", true)]
    [InlineData("ISREF($B$2)", true)]
    [InlineData("ISREF(A1:B2)", true)]
    [InlineData("ISREF((A1))", true)]          // R12
    [InlineData("ISREF(((A1:B2)))", true)]     // R12
    [InlineData("ISREF(A1+0)", false)]
    [InlineData("ISREF(-A1)", false)]
    [InlineData("ISREF(ID(A1))", false)]
    [InlineData("ISREF(1)", false)]
    [InlineData("ISREF(\"A1\")", false)]
    [InlineData("ISREF(foo)", false)]
    [InlineData("ISREF(,)", false)]
    public void IsReference(string formula, bool expected) => Assert.Equal(B(expected), Run(formula));

    [Fact]
    public void ReferenceArgument_IgnoresTextButDirectArgumentCoerces()
    {
        var cells = new DictionaryCells().Set("A1", "5").Set("A2", 2).Set("A3", true);
        Assert.Equal(N(2), Run("FSUM(A1:A3)", cells));
        Assert.Equal(N(2), Run("FSUM(A1,A2)", cells));
        Assert.Equal(N(2), Run("FSUM((A1),A2)", cells));    // R12: still a reference
        Assert.Equal(N(7), Run("FSUM(A1&\"\",A2)", cells)); // direct text coerces
        Assert.Equal(N(8), Run("FSUM(\"5\",A2,TRUE)", cells));
    }

    [Fact]
    public void EnumerateReferenceValues_RowMajor_WithBlankFlag()
    {
        var cells = new DictionaryCells().Set("B1", 1).Set("A2", "").Set("B3", 0);
        Assert.Equal(T("A1,B1,A2,B2,A3,B3"), Run("ADDRS(A1:B3)", cells));
        Assert.Equal(T("A1,B1,A2,B2,A3,B3"), Run("ADDRS(B3:A1)", cells));  // normalised
        Assert.Equal(T("C7"), Run("ADDRS($C$7)", cells));
        Assert.Equal(N(3), Run("COUNTBLANKS(A1:B3)", cells));             // A2 holds "" so it is not blank
    }

    [Fact]
    public void EnumerateReferenceValues_OnDirectArgument_Throws()
    {
        var fns = new FakeFunctions();
        Assert.Throws<InvalidOperationException>(() => Run("COUNTBLANKS(1)", functions: fns));
    }

    [Fact]
    public void ReferenceValues_CanBeBuiltDirectly_ForFunctionTests()
    {
        var cells = new DictionaryCells().Set("A1", 4);
        var values = new ReferenceValues(cells, CellRange.Parse("A1:A2"));
        Assert.Equal(2, values.Count);
        var list = new List<ReferenceValue>();
        foreach (var v in values) list.Add(v);
        Assert.Equal(2, list.Count);
        Assert.Equal(N(4), list[0].Value);
        Assert.False(list[0].IsBlank);
        Assert.True(list[1].IsBlank);
        Assert.Equal(CellAddress.Parse("A2"), list[1].Address);
    }

    [Fact]
    public void Arguments_AreLazy()
    {
        var fns = new FakeFunctions();
        Assert.Equal(N(10), Run("FIF(TRUE,10,COUNTER())", functions: fns));
        Assert.Equal(0, fns.CounterCalls);
        Assert.Equal(N(1), Run("FIF(FALSE,10,COUNTER())", functions: fns));
        Assert.Equal(1, fns.CounterCalls);
        Assert.Equal(N(5), Run("FIF(TRUE,5,1/0)", functions: fns));  // the unused error never surfaces
    }

    [Fact]
    public void ArgumentErrors_AreValues_NotPropagatedAutomatically()
    {
        var fns = new FakeFunctions();
        fns.Add("ISERR", a => CellValue.FromBoolean(a.Evaluate(0).Kind == ValueKind.Error));
        Assert.Equal(B(true), Run("ISERR(1/0)", functions: fns));
        Assert.Equal(B(true), Run("ISERR(A1:A2)", functions: fns));   // §6.5 range -> #VALUE! as a value
        Assert.Equal(B(false), Run("ISERR(A1)", functions: fns));
    }

    [Fact]
    public void Evaluate_BlankCellReference_IsBlank()
    {
        var fns = new FakeFunctions();
        fns.Add("KIND", a => CellValue.FromText(a.Evaluate(0).Kind.ToString()));
        Assert.Equal(T("Blank"), Run("KIND(A1)", functions: fns));
        Assert.Equal(T("Blank"), Run("KIND((A1))", functions: fns));
    }

    [Fact]
    public void Arguments_InvalidAfterInvoke()
    {
        var fns = new FakeFunctions();
        Run("STASH(1)", functions: fns);
        Assert.NotNull(fns.Stashed);
        Assert.Throws<InvalidOperationException>(() => fns.Stashed!.Evaluate(0));
        Assert.Throws<InvalidOperationException>(() => fns.Stashed!.Count);
    }

    [Fact]
    public void Arguments_IndexOutOfRange_Throws()
    {
        var fns = new FakeFunctions();
        fns.Add("BAD", a => a.Evaluate(a.Count));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run("BAD(1)", functions: fns));
        fns.Add("BADNEG", a => CellValue.FromBoolean(a.IsReference(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run("BADNEG(1)", functions: fns));
    }

    [Fact]
    public void NestedLazyEvaluation_InsideOperators()
    {
        var cells = new DictionaryCells().Set("A1", 2);
        Assert.Equal(N(9), Run("1+ID(ID(A1)*ID(2+2))", cells));
        Assert.Equal(T("2x"), Run("ID(A1)&ID(\"x\")", cells));
    }
}
