using Gridline.Core.Addressing;
using Gridline.Core.Evaluation;
using Gridline.Core.Parsing;
using static Gridline.Core.Tests.Evaluation.Eval;

namespace Gridline.Core.Tests.Evaluation;

// Requirement IDs: §9.6 (name evaluation), §7.0 and ruling P2 (a name is a reference argument), §6.5 (a range name in
// scalar context), §5.1 (undefined name → #NAME?), ruling P1 (a deleted target evaluates to #REF!).
public class NameEvaluationTests
{
    private sealed class DictionaryNames : INameResolver
    {
        private readonly Dictionary<string, NameTarget> _map = new(StringComparer.OrdinalIgnoreCase);

        public DictionaryNames Cell(string name, string address)
        {
            var a = CellAddress.Parse(address);
            _map[name] = new NameTarget(NameTargetKind.Cell, new CellRange(a, a));
            return this;
        }

        public DictionaryNames Range(string name, string range)
        {
            _map[name] = new NameTarget(NameTargetKind.Range, CellRange.Parse(range));
            return this;
        }

        public DictionaryNames Deleted(string name)
        {
            _map[name] = NameTarget.Invalid;
            return this;
        }

        public bool TryResolveName(string name, out NameTarget target) => _map.TryGetValue(name, out target);
    }

    private static CellValue RunNames(string formula, DictionaryCells cells, DictionaryNames names)
    {
        var parsed = FormulaParser.Parse(formula, null);
        Assert.True(parsed.Success, parsed.ErrorMessage);
        return FormulaEvaluator.Evaluate(parsed, cells, new FakeFunctions(), names);
    }

    private static readonly DictionaryCells Sheet = new DictionaryCells()
        .Set("B1", 5).Set("A1", 1).Set("A2", "text").Set("A3", true).Set("A4", 2);

    private static readonly DictionaryNames Names = new DictionaryNames()
        .Cell("Rate", "B1").Range("Total", "A1:A4").Range("One", "B1:B1").Cell("Empty", "Z9").Deleted("Gone");

    [Theory]
    [InlineData("Rate*2", 10)]
    [InlineData("rate*2", 10)]   // case-insensitive
    [InlineData("(RATE)+1", 6)]
    [InlineData("Empty", 0)]     // §6.6 blank target as a scalar
    [InlineData("FSUM(Total)", 3)] // reference: text and boolean ignored (§7.0)
    [InlineData("FSUM((Total))", 3)] // R12 parentheses keep it a reference
    [InlineData("FSUM(Rate,Total)", 8)]
    public void DefinedNames_BehaveLikeTheirReferences(string formula, double expected) =>
        Assert.Equal(N(expected), RunNames(formula, Sheet, Names));

    [Theory]
    [InlineData("Total")]
    [InlineData("Total+1")]
    [InlineData("One")]   // a 1x1 range name is still a range (R58, §6.5)
    [InlineData("ID(Total)")]
    public void RangeName_InScalarContext_IsValue(string formula) =>
        Assert.Equal(E(ErrorCodes.Value), RunNames(formula, Sheet, Names));

    [Fact]
    public void NameArgument_IsAReference_WhileItHasALiveTarget()
    {
        Assert.Equal(B(true), RunNames("ISREF(Rate)", Sheet, Names));
        Assert.Equal(B(true), RunNames("ISREF(Total)", Sheet, Names));
        Assert.Equal(B(false), RunNames("ISREF(Nope)", Sheet, Names));
        Assert.Equal(B(false), RunNames("ISREF(Gone)", Sheet, Names));
        Assert.Equal(T("A1,A2,A3,A4"), RunNames("ADDRS(Total)", Sheet, Names));
        Assert.Equal(N(1), RunNames("COUNTBLANKS(Empty)", Sheet, Names));
    }

    [Fact]
    public void UndefinedName_IsName_AndDeletedTarget_IsRef()
    {
        Assert.Equal(E(ErrorCodes.Name), RunNames("Nope", Sheet, Names));
        Assert.Equal(E(ErrorCodes.Name), RunNames("FSUM(Nope)", Sheet, Names)); // direct argument, error propagates
        Assert.Equal(E(ErrorCodes.Ref), RunNames("Gone", Sheet, Names));
        Assert.Equal(E(ErrorCodes.Ref), RunNames("FSUM(1,Gone)", Sheet, Names));
        Assert.Equal(E(ErrorCodes.Name), Run("Rate", Sheet)); // no resolver: no names
    }

    // §9.6, §5.1 (controller ruling, Task 1 review): where a reference is required, an undefined name gives #NAME? and a
    // deleted target #REF!, never the function's #VALUE!; ISBLANK never errors.
    [Theory]
    [InlineData("COUNTBLANK(Nope)", ErrorCodes.Name)]
    [InlineData("COUNTBLANK((Nope))", ErrorCodes.Name)]
    [InlineData("COUNTBLANK(Gone)", ErrorCodes.Ref)]
    [InlineData("COUNTBLANK(1)", ErrorCodes.Value)]
    public void BuiltIns_ReferenceRequiredPosition_GivesTheNamesOwnError(string formula, string code)
    {
        var parsed = FormulaParser.Parse(formula, null);
        Assert.Equal(E(code), FormulaEvaluator.Evaluate(parsed, Sheet, Gridline.Core.Functions.BuiltInFunctionTable.Default, Names));
    }

    [Theory]
    [InlineData("ISBLANK(Nope)", false)]
    [InlineData("ISBLANK(Gone)", false)]
    [InlineData("ISBLANK(Empty)", true)]
    [InlineData("ISBLANK(Rate)", false)]
    [InlineData("COUNTBLANK(Empty)=1", true)]
    public void BuiltIns_ThroughNames(string formula, bool expected)
    {
        var parsed = FormulaParser.Parse(formula, null);
        Assert.Equal(B(expected), FormulaEvaluator.Evaluate(parsed, Sheet, Gridline.Core.Functions.BuiltInFunctionTable.Default, Names));
    }

    [Fact]
    public void TryGetReferenceError_IsTrueOnlyForUnusableNames()
    {
        var functions = new FakeFunctions();
        functions.Add("REFERR", a => a.TryGetReferenceError(0, out var e) ? e : CellValue.FromText(a.IsReference(0) ? "ref" : "direct"));
        CellValue RunRef(string f) => FormulaEvaluator.Evaluate(FormulaParser.Parse(f, null), Sheet, functions, Names);
        Assert.Equal(E(ErrorCodes.Name), RunRef("REFERR(Nope)"));
        Assert.Equal(E(ErrorCodes.Ref), RunRef("REFERR((Gone))"));
        Assert.Equal(T("ref"), RunRef("REFERR(Rate)"));
        Assert.Equal(T("ref"), RunRef("REFERR(Total)"));
        Assert.Equal(T("ref"), RunRef("REFERR(A1)"));
        Assert.Equal(T("direct"), RunRef("REFERR(1+1)"));
        Assert.Equal(T("direct"), RunRef("REFERR(#NAME?)"));
    }

    [Fact]
    public void UnderscoreName_EvaluatesThroughTheResolver() // R57
    {
        var names = new DictionaryNames().Cell("_x", "B1");
        Assert.Equal(N(5), RunNames("_x", Sheet, names));
        Assert.Equal(E(ErrorCodes.Name), RunNames("_y", Sheet, names));
    }
}
