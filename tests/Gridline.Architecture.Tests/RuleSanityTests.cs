using Gridline.Architecture.Tests.Violations;
using NetArchTest.Rules;
using static Gridline.Architecture.Tests.ArchSupport;

namespace Gridline.Architecture.Tests;

/// <summary>Each rule mechanism must fail on a known violation (§3.2, §3.3), and must not flag an allowed dependency.</summary>
public class RuleSanityTests
{
    private static readonly System.Reflection.Assembly Self = typeof(RuleSanityTests).Assembly;
    private const string Fixtures = "Gridline.Architecture.Tests.RuleViolations.";

    private static string BadNs(string owner) => Fixtures + "Bad." + owner;
    private static string GoodNs(string owner) => Fixtures + "Good." + owner;

    // ---- §3.3 mutable-static checker ----

    [Fact]
    public void MutableStaticChecker_FlagsKnownViolations()
    {
        var found = FindPublicMutableStatics([typeof(BadStatics)]);
        Assert.Contains(found, s => s.Contains("Counter"));
        Assert.Contains(found, s => s.Contains(".Array"));
        Assert.Contains(found, s => s.Contains(".List"));
        Assert.Contains(found, s => s.Contains("Setter"));
        Assert.Contains(found, s => s.Contains("GetOnlyArray"));
        Assert.Contains(found, s => s.Contains("GetOnlyList"));
        Assert.Contains(found, s => s.Contains("Changed") && s.Contains("event"));
        Assert.DoesNotContain(found, s => s.Contains("InitOnly") || s.Contains("Constant") || s.Contains("ReadonlyString") || s.Contains("Immutable")
            || s.Contains("ReadOnlyWrapper") || s.Contains("ReadOnlyInterface"));
        Assert.Equal(7, found.Count);
    }

    [Fact]
    public void MutableStaticChecker_FlagsProtectedStatics()
    {
        var found = FindPublicMutableStatics([typeof(BadProtectedStatics)]);
        Assert.Contains(found, s => s.Contains("Counter"));
        Assert.Contains(found, s => s.Contains("Items"));
        Assert.Contains(found, s => s.Contains("Setter"));
        Assert.Contains(found, s => s.Contains("Changed") && s.Contains("event"));
        Assert.DoesNotContain(found, s => s.Contains("Fine") || s.Contains("Constant"));
        Assert.Equal(4, found.Count);
    }

    [Fact]
    public void MutableStaticChecker_AllowsInstanceInitOnlyAndNonPublicStatics()
    {
        Assert.Empty(FindPublicMutableStatics([typeof(GoodInstanceAndPrivate)]));
        // init accessors cannot be static in C#, so an init-only property can only exist on instances; the checker
        // also tolerates a static property whose setter carries the IsExternalInit modifier (see ArchSupport).
        Assert.NotNull(typeof(GoodInstanceAndPrivate).GetProperty(nameof(GoodInstanceAndPrivate.InitOnly)));
    }

    // ---- §3.2 forbidden-type rules ----

    [Theory]
    [InlineData("System.IO.File")]
    [InlineData("System.IO.Directory")]
    [InlineData("System.IO.FileStream")]
    [InlineData("System.Console")]
    [InlineData("Gridline.Core")]
    public void DependencyRule_FailsOnKnownViolation(string forbidden)
    {
        var r = Types.InAssembly(Self).That().ResideInNamespace(typeof(UsesForbiddenTypes).Namespace!)
            .ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        Assert.False(r.IsSuccessful, forbidden + " violation was not detected");
    }

    private static bool Flags(Type type, string forbidden) =>
        !Types.InAssembly(Self).That().HaveName(type.Name).ShouldNot().HaveDependencyOnAny(forbidden).GetResult().IsSuccessful;

    // Each forbidden name is flagged on the type that uses only it, and not on types that use only a different one.
    [Theory]
    [InlineData("System.IO.File", typeof(UsesOnlyFile), true)]
    [InlineData("System.IO.File", typeof(UsesOnlyDirectory), false)]
    [InlineData("System.IO.File", typeof(UsesOnlyConsole), false)]
    [InlineData("System.IO.File", typeof(UsesNeitherForbidden), false)]
    [InlineData("System.IO.Directory", typeof(UsesOnlyDirectory), true)]
    [InlineData("System.IO.Directory", typeof(UsesOnlyFile), false)]
    [InlineData("System.IO.Directory", typeof(UsesOnlyConsole), false)]
    [InlineData("System.IO.Directory", typeof(UsesNeitherForbidden), false)]
    [InlineData("System.IO.FileStream", typeof(UsesOnlyFileStream), true)]
    [InlineData("System.IO.FileStream", typeof(UsesOnlyDirectory), false)]
    [InlineData("System.IO.FileStream", typeof(UsesNeitherForbidden), false)]
    [InlineData("System.Console", typeof(UsesOnlyConsole), true)]
    [InlineData("System.Console", typeof(UsesOnlyFile), false)]
    [InlineData("System.Console", typeof(UsesOnlyDirectory), false)]
    [InlineData("System.Console", typeof(UsesNeitherForbidden), false)]
    public void ForbiddenTypeRule_Discriminates(string forbidden, Type type, bool expectFlagged) =>
        Assert.Equal(expectFlagged, Flags(type, forbidden));

    // D-29: NetArchTest 1.3.2 matches the dependency name as a whole type name, not as a string prefix. The §3.2
    // rules therefore ban exactly File, Directory, FileStream and Console; FileInfo, DirectoryInfo and ConsoleColor
    // are NOT caught. This test pins that behaviour so a NetArchTest upgrade that changes it is noticed.
    [Theory]
    [InlineData("System.IO.File", typeof(UsesOnlyFileInfo))]
    [InlineData("System.IO.Directory", typeof(UsesOnlyDirectoryInfo))]
    [InlineData("System.Console", typeof(UsesOnlyConsoleColor))]
    public void ForbiddenTypeRule_IsExactNameNotPrefix(string forbidden, Type type) =>
        Assert.False(Flags(type, forbidden), $"{type.Name} unexpectedly flagged by {forbidden}: matching is no longer exact");

    // ---- §3.3 namespace rules, through the same SiblingRule helper as NamespaceRuleTests ----

    public static TheoryData<string, string> ForbiddenPairs()
    {
        var data = new TheoryData<string, string>();
        foreach (var (owner, forbidden) in NamespaceRules)
            foreach (var sibling in forbidden) data.Add(owner, sibling);
        return data;
    }

    // Every (owner, forbidden sibling) pair of the rule table has a Bad type that the helper flags.
    [Theory]
    [MemberData(nameof(ForbiddenPairs))]
    public void NamespaceRule_FailsOnKnownViolation(string owner, string forbiddenSibling)
    {
        var bad = SiblingRule(Self, BadNs(owner), forbiddenSibling);
        Assert.False(bad.IsSuccessful, $"{owner} -> {forbiddenSibling} violation was not detected");
        Assert.Contains(Self.GetType(BadNs(owner) + ".Uses" + forbiddenSibling)!.FullName!, bad.FailingTypeNames!);
    }

    // Naming check that keeps the fixtures aligned with the table: each Bad.<Owner> type name matches a forbidden sibling.
    [Theory]
    [MemberData(nameof(ForbiddenPairs))]
    public void NamespaceRule_FixtureTypeExists(string owner, string forbiddenSibling) =>
        Assert.NotNull(Self.GetType(BadNs(owner) + ".Uses" + forbiddenSibling));

    // The whole rule at once, exactly as the production test calls it: it fails on the Bad namespace and passes on Good.
    [Theory]
    [InlineData("Addressing")]
    [InlineData("Parsing")]
    [InlineData("Evaluation")]
    [InlineData("Functions")]
    [InlineData("Dependencies")]
    public void NamespaceRule_WholeRuleFailsOnBadAndPassesOnGood(string owner)
    {
        var bad = SiblingRule(Self, BadNs(owner), [.. NamespaceRules[owner]]);
        Assert.False(bad.IsSuccessful);
        var good = SiblingRule(Self, GoodNs(owner), [.. NamespaceRules[owner]]);
        Assert.True(good.IsSuccessful, $"allowed dependencies of {owner} were flagged. " + Describe(good));
    }

    // Negative controls: the Good fixtures really use the allowed namespaces (so "not flagged" is not vacuous).
    [Theory]
    [InlineData("Parsing", "Addressing")]
    [InlineData("Evaluation", "Parsing")]
    [InlineData("Evaluation", "Functions")]
    [InlineData("Evaluation", "Addressing")]
    [InlineData("Functions", "Evaluation")]
    [InlineData("Functions", "Addressing")]
    [InlineData("Dependencies", "Parsing")]
    [InlineData("Dependencies", "Evaluation")]
    [InlineData("Dependencies", "Addressing")]
    public void NamespaceRule_AllowedDependencyIsNotFlagged(string owner, string allowedSibling)
    {
        Assert.DoesNotContain(allowedSibling, NamespaceRules[owner]);
        // The helper does flag the dependency if it were forbidden ...
        var wouldFlag = SiblingRule(Self, GoodNs(owner), allowedSibling);
        Assert.False(wouldFlag.IsSuccessful, $"fixture Good.{owner} does not actually use {allowedSibling}");
        // ... and the real rule, which does not list it, passes.
        var ok = SiblingRule(Self, GoodNs(owner), [.. NamespaceRules[owner]]);
        Assert.True(ok.IsSuccessful, Describe(ok));
    }

    [Fact]
    public void NamespaceRule_AddressingRootTypeRule()
    {
        var bad = NamespaceMustNotDependOn(Self, BadNs("Addressing"), CoreRootTypeNames());
        Assert.False(bad.IsSuccessful);
        Assert.Contains(BadNs("Addressing") + ".UsesRootType", bad.FailingTypeNames!);
        var good = NamespaceMustNotDependOn(Self, GoodNs("Addressing"), CoreRootTypeNames());
        Assert.True(good.IsSuccessful, Describe(good));
        Assert.NotEmpty(Types.InAssembly(Self).That().ResideInNamespaceStartingWith(GoodNs("Addressing")).GetTypes());
    }

    // The §3.3 text, pinned: the table that drives both the production and the sanity tests lists exactly these.
    [Fact]
    public void NamespaceRuleTable_MatchesSpec33()
    {
        Assert.Equal(["Commands", "Dependencies", "Evaluation", "Functions", "Parsing"], NamespaceRules["Addressing"].Order());
        Assert.Equal(["Commands", "Dependencies", "Evaluation", "Functions"], NamespaceRules["Parsing"].Order());
        Assert.Equal(["Commands", "Dependencies"], NamespaceRules["Evaluation"].Order());
        Assert.Equal(["Commands", "Dependencies", "Parsing"], NamespaceRules["Functions"].Order());
        Assert.Equal(["Commands", "Functions"], NamespaceRules["Dependencies"].Order());
        Assert.Equal(5, NamespaceRules.Count);
    }

    [Fact]
    public void RootTypeRule_FailsOnKnownViolation()
    {
        var r = NamespaceMustNotDependOn(Self, typeof(UsesForbiddenTypes).Namespace!, CoreRootTypeNames());
        Assert.False(r.IsSuccessful);
    }

    [Fact]
    public void FileRule_DiscriminatesFileFromFileStream()
    {
        var r = Types.InAssembly(Self).That().HaveName(nameof(UsesOnlyFile))
            .ShouldNot().HaveDependencyOnAny("System.IO.File").GetResult();
        Assert.False(r.IsSuccessful);
    }

    [Fact]
    public void ReferenceRules_SeeGridlineReferences()
    {
        Assert.Contains("Gridline.Core", ReferencedGridlineAssemblies(Self));
        Assert.Contains("Gridline.Core", ProjectReferences("Gridline.IO"));
    }

    [Fact]
    public void RootTypeEnumeration_IsNonVacuous() => Assert.Contains("Gridline.Core.Workbook", CoreRootTypeNames());
}
