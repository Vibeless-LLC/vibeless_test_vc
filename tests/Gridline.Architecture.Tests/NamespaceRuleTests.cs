using NetArchTest.Rules;
using static Gridline.Architecture.Tests.ArchSupport;

namespace Gridline.Architecture.Tests;

/// <summary>§3.3 namespace rules inside Gridline.Core.
/// Root-namespace decision (DECISIONS D-29): types directly in Gridline.Core (Workbook, CellValue, ...) count as
/// "another Core namespace" for Addressing, so Addressing may not use them either. For the other rules the root is
/// not in the forbidden lists, so it is unrestricted. Prefix matching: "Gridline.Core" would match every
/// sub-namespace, so siblings are enumerated and root types are listed by full name. NetArchTest 1.3.2 never
/// matches a namespace given with a trailing dot, so bare names are used and prefix collisions are guarded by a test.</summary>
public class NamespaceRuleTests
{
    private static readonly string[] Siblings = [.. CoreNamespaces];

    private static void AssertNoDeps(string ns, string[] forbiddenSubNamespaces)
    {
        var r = SiblingRule(CoreAsm, Ns(ns), forbiddenSubNamespaces);
        Assert.True(r.IsSuccessful, Describe(r));
    }

    // §3.3: Addressing depends on no other Core namespace.
    [Fact]
    public void S3_3_Addressing_DependsOnNoOtherCoreNamespace()
    {
        AssertNoDeps("Addressing", [.. NamespaceRules["Addressing"]]);
        var rootTypes = CoreRootTypeNames();
        Assert.NotEmpty(rootTypes);
        var r = NamespaceMustNotDependOn(CoreAsm, Ns("Addressing"), rootTypes);
        Assert.True(r.IsSuccessful, "Addressing depends on a Core root type. " + Describe(r));
    }

    // §3.3: Parsing MUST NOT depend on Evaluation, Functions, Dependencies or Commands.
    [Fact]
    public void S3_3_Parsing_DoesNotDependOnEvaluationFunctionsDependenciesCommands() =>
        AssertNoDeps("Parsing", [.. NamespaceRules["Parsing"]]);

    // §3.3: Evaluation MUST NOT depend on Commands or Dependencies.
    [Fact]
    public void S3_3_Evaluation_DoesNotDependOnCommandsOrDependencies() =>
        AssertNoDeps("Evaluation", [.. NamespaceRules["Evaluation"]]);

    // §3.3: Functions MUST NOT depend on Parsing, Dependencies or Commands.
    [Fact]
    public void S3_3_Functions_DoesNotDependOnParsingDependenciesCommands() =>
        AssertNoDeps("Functions", [.. NamespaceRules["Functions"]]);

    // §3.3: Dependencies MUST NOT depend on Functions or Commands.
    [Fact]
    public void S3_3_Dependencies_DoesNotDependOnFunctionsOrCommands() =>
        AssertNoDeps("Dependencies", [.. NamespaceRules["Dependencies"]]);

    // Prefix-collision guard: bare namespace prefixes are safe only if no Core namespace merely starts with a sibling name.
    [Fact]
    public void S3_3_NoCoreNamespaceCollidesWithSiblingPrefix()
    {
        foreach (var t in CoreAsm.GetTypes())
            foreach (var sib in Siblings)
            {
                var p = Ns(sib);
                var n = t.Namespace ?? "";
                Assert.False(n.StartsWith(p, StringComparison.Ordinal) && n != p && !n.StartsWith(p + ".", StringComparison.Ordinal),
                    $"{t.FullName} is in a namespace that collides with prefix {p}");
            }
    }

    // Guard against vacuous namespace rules: every rule target namespace has types (Commands since Phase 3 Task 3).
    [Theory]
    [InlineData("Addressing")]
    [InlineData("Parsing")]
    [InlineData("Evaluation")]
    [InlineData("Functions")]
    [InlineData("Dependencies")]
    [InlineData("Commands")]
    public void S3_3_RuleTargetNamespaces_AreNotEmpty(string ns) =>
        Assert.NotEmpty(Types.InAssembly(CoreAsm).That().ResideInNamespaceStartingWith(Ns(ns)).GetTypes());

    // §3.3: no public mutable static state anywhere in src/.
    [Fact]
    public void S3_3_Src_HasNoPublicMutableStaticState()
    {
        var found = FindPublicMutableStatics(AllSrc.SelectMany(a => a.GetTypes()));
        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
    }
}
