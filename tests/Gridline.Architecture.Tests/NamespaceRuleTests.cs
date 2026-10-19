using NetArchTest.Rules;
using static Gridline.Architecture.Tests.ArchSupport;

namespace Gridline.Architecture.Tests;

/// <summary>§3.3 namespace rules inside Gridline.Core.
/// Root-namespace decision (DECISIONS D-29): types directly in Gridline.Core (Workbook, CellValue, ...) count as
/// "another Core namespace" for Addressing, so Addressing may not use them either. For the other rules the root is
/// not in the forbidden lists, so it is unrestricted. Prefix matching: "Gridline.Core" would match every
/// sub-namespace, so siblings are enumerated (with a trailing dot) and root types are listed by full name.</summary>
public class NamespaceRuleTests
{
    private static readonly string[] Siblings = ["Addressing", "Parsing", "Evaluation", "Functions", "Dependencies", "Commands"];

    private static void AssertNoDeps(string ns, params string[] forbiddenSubNamespaces)
    {
        var r = NamespaceMustNotDependOn(CoreAsm, Ns(ns), forbiddenSubNamespaces.Select(n => Ns(n) + ".").ToArray());
        Assert.True(r.IsSuccessful, Describe(r));
    }

    // §3.3: Addressing depends on no other Core namespace.
    [Fact]
    public void S3_3_Addressing_DependsOnNoOtherCoreNamespace()
    {
        AssertNoDeps("Addressing", Siblings.Where(s => s != "Addressing").ToArray());
        var rootTypes = CoreRootTypeNames();
        Assert.NotEmpty(rootTypes);
        var r = NamespaceMustNotDependOn(CoreAsm, Ns("Addressing"), rootTypes);
        Assert.True(r.IsSuccessful, "Addressing depends on a Core root type. " + Describe(r));
    }

    // §3.3: Parsing MUST NOT depend on Evaluation, Functions, Dependencies or Commands.
    [Fact]
    public void S3_3_Parsing_DoesNotDependOnEvaluationFunctionsDependenciesCommands() =>
        AssertNoDeps("Parsing", "Evaluation", "Functions", "Dependencies", "Commands");

    // §3.3: Evaluation MUST NOT depend on Commands or Dependencies.
    [Fact]
    public void S3_3_Evaluation_DoesNotDependOnCommandsOrDependencies() =>
        AssertNoDeps("Evaluation", "Commands", "Dependencies");

    // §3.3: Functions MUST NOT depend on Parsing, Dependencies or Commands.
    [Fact]
    public void S3_3_Functions_DoesNotDependOnParsingDependenciesCommands() =>
        AssertNoDeps("Functions", "Parsing", "Dependencies", "Commands");

    // §3.3: Dependencies MUST NOT depend on Functions or Commands.
    [Fact]
    public void S3_3_Dependencies_DoesNotDependOnFunctionsOrCommands() =>
        AssertNoDeps("Dependencies", "Functions", "Commands");

    // Guard against vacuous namespace rules: every namespace except Commands (still empty in Phase 1) has types.
    [Theory]
    [InlineData("Addressing")]
    [InlineData("Parsing")]
    [InlineData("Evaluation")]
    [InlineData("Functions")]
    [InlineData("Dependencies")]
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
