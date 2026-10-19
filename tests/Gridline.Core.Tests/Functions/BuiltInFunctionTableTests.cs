using Gridline.Core.Evaluation;
using Gridline.Core.Functions;
using Gridline.Core.Tests.Evaluation;
using static Gridline.Core.Tests.Functions.Fx;

namespace Gridline.Core.Tests.Functions;

// Requirement IDs: §7.0, §7.1, R3, R16 (registration table, arity adapter), §5.4 (#PARSE! for wrong arity), §6.8 (#NAME?).
public class BuiltInFunctionTableTests
{
    private static readonly string[] MathNames =
    [
        "SUM", "AVERAGE", "MIN", "MAX", "PRODUCT", "COUNT", "COUNTA", "COUNTBLANK", "SUMPRODUCT", "ABS",
        "SIGN", "INT", "MOD", "POWER", "SQRT", "PI", "ROUND", "ROUNDUP", "ROUNDDOWN", "SUMSQ",
    ];

    [Fact]
    public void AllTwentyMathFunctionsAreRegistered()
    {
        var table = BuiltInFunctionTable.Default;
        foreach (var name in MathNames)
        {
            Assert.True(table.TryResolve(name, out var fn), name);
            Assert.NotNull(fn);
            Assert.True(table.TryGetArity(name, out _, out _), name);
        }
    }

    [Theory]
    [InlineData("SUM", 1, 255)]
    [InlineData("AVERAGE", 1, 255)]
    [InlineData("MIN", 1, 255)]
    [InlineData("MAX", 1, 255)]
    [InlineData("PRODUCT", 1, 255)]
    [InlineData("COUNT", 1, 255)]
    [InlineData("COUNTA", 1, 255)]
    [InlineData("COUNTBLANK", 1, 1)]
    [InlineData("SUMPRODUCT", 1, 255)]
    [InlineData("SUMSQ", 1, 255)]
    [InlineData("ABS", 1, 1)]
    [InlineData("SIGN", 1, 1)]
    [InlineData("INT", 1, 1)]
    [InlineData("SQRT", 1, 1)]
    [InlineData("MOD", 2, 2)]
    [InlineData("POWER", 2, 2)]
    [InlineData("ROUND", 2, 2)]
    [InlineData("ROUNDUP", 2, 2)]
    [InlineData("ROUNDDOWN", 2, 2)]
    [InlineData("PI", 0, 0)]
    public void Arity(string name, int min, int max)
    {
        Assert.True(BuiltInFunctionTable.Default.TryGetArity(name, out var lo, out var hi));
        Assert.Equal((min, max), (lo, hi));
        Assert.Equal(255, BuiltInFunctionTable.MaxVariadicArguments);
    }

    [Fact]
    public void AdapterMirrorsTable()
    {
        var adapter = new FunctionArityAdapter();
        Assert.True(adapter.TryGetArity("SUM", out var min, out var max));
        Assert.Equal((1, 255), (min, max));
        Assert.False(adapter.TryGetArity("NOSUCH", out _, out _));
    }

    [Fact]
    public void Definitions_AreUniqueAndUpperCase()
    {
        var names = BuiltInFunctionTable.Default.Definitions.Select(d => d.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, n => Assert.Equal(n.ToUpperInvariant(), n));
        Assert.All(BuiltInFunctionTable.Default.Definitions, d => Assert.InRange(d.MinArguments, 0, d.MaxArguments));
    }

    [Fact]
    public void DefaultTable_IsSharedInstance() =>
        Assert.Same(BuiltInFunctionTable.Default, BuiltInFunctionTable.Default);

    [Fact]
    public void Builder_RejectsDuplicatesAndBadArity()
    {
        var builder = new BuiltInFunctionTable.Builder();
        var fn = new DelegateFunction(_ => CellValue.Blank);
        builder.Add("X", 0, 1, fn);
        Assert.Throws<ArgumentException>(() => builder.Add("X", 0, 1, fn));
        Assert.Throws<ArgumentException>(() => builder.Add("Y", 2, 1, fn));
        Assert.Throws<ArgumentException>(() => builder.Add("Z", 0, 256, fn));
        Assert.Throws<ArgumentException>(() => builder.Add("lower", 0, 1, fn));
    }

    // ---- Wire-up: parser + adapter -> evaluator + table ----

    [Fact]
    public void EndToEnd_ThroughParserAndEvaluator()
    {
        Assert.Equal(N(2.68), Run("ROUND(2.675,2)"));
        Assert.Equal(N(6), Run("SUM(1,2,3)"));
        Assert.Equal(N(4), Run("1+SUM(1,2)+A1/2", new DictionaryCells().Set("A1", 0)));
    }

    [Theory]
    [InlineData("ABS(1,2)")]
    [InlineData("ABS()")]
    [InlineData("PI(1)")]
    [InlineData("SUM()")]
    [InlineData("MOD(1)")]
    [InlineData("ROUND(1,2,3)")]
    [InlineData("COUNTBLANK(A1,B1)")]
    public void WrongArity_IsParseError(string formula) => Assert.Equal(E(ErrorCodes.Parse), Run(formula));

    [Fact]
    public void Sum_256Arguments_IsParseError_255IsFine()
    {
        Assert.Equal(N(255), Run("SUM(" + string.Join(",", Enumerable.Repeat("1", 255)) + ")"));
        Assert.Equal(E(ErrorCodes.Parse), Run("SUM(" + string.Join(",", Enumerable.Repeat("1", 256)) + ")"));
    }

    [Fact]
    public void UnknownFunction_IsNameError() => Assert.Equal(E(ErrorCodes.Name), Run("NOSUCHFN(1)"));
}
