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
            || s.Contains("ReadOnlyWrapper") || s.Contains("ReadOnlyInterface") || s.Contains("Frozen") || s.Contains("EnumerableProp")
            || s.Contains("ReadOnlyDictionaryProp") || s.Contains("ReadOnlyCollectionProp") || s.Contains("ComparerIfaceProp") || s.Contains("ComparerProp")
            || s.Contains("PairProp"));
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

    // Each forbidden name (§3.2, R37) is flagged on the type that uses only it and on no other fixture: the full
    // grid, so File vs FileStream vs FileInfo vs FileSystemInfo and Directory vs DirectoryInfo are all pinned.
    // (UsesOnlyConsoleColor is covered by the separate "not Console" test below.)
    [Theory]
    [InlineData("System.IO.File", typeof(UsesOnlyFile), true)]
    [InlineData("System.IO.File", typeof(UsesOnlyDirectory), false)]
    [InlineData("System.IO.File", typeof(UsesOnlyFileStream), false)]
    [InlineData("System.IO.File", typeof(UsesOnlyFileInfo), false)]
    [InlineData("System.IO.File", typeof(UsesOnlyDirectoryInfo), false)]
    [InlineData("System.IO.File", typeof(UsesOnlyFileSystemInfo), false)]
    [InlineData("System.IO.File", typeof(UsesOnlyConsole), false)]
    [InlineData("System.IO.File", typeof(UsesNeitherForbidden), false)]
    [InlineData("System.IO.Directory", typeof(UsesOnlyFile), false)]
    [InlineData("System.IO.Directory", typeof(UsesOnlyDirectory), true)]
    [InlineData("System.IO.Directory", typeof(UsesOnlyFileStream), false)]
    [InlineData("System.IO.Directory", typeof(UsesOnlyFileInfo), false)]
    [InlineData("System.IO.Directory", typeof(UsesOnlyDirectoryInfo), false)]
    [InlineData("System.IO.Directory", typeof(UsesOnlyFileSystemInfo), false)]
    [InlineData("System.IO.Directory", typeof(UsesOnlyConsole), false)]
    [InlineData("System.IO.Directory", typeof(UsesNeitherForbidden), false)]
    [InlineData("System.IO.FileStream", typeof(UsesOnlyFile), false)]
    [InlineData("System.IO.FileStream", typeof(UsesOnlyDirectory), false)]
    [InlineData("System.IO.FileStream", typeof(UsesOnlyFileStream), true)]
    [InlineData("System.IO.FileStream", typeof(UsesOnlyFileInfo), false)]
    [InlineData("System.IO.FileStream", typeof(UsesOnlyDirectoryInfo), false)]
    [InlineData("System.IO.FileStream", typeof(UsesOnlyFileSystemInfo), false)]
    [InlineData("System.IO.FileStream", typeof(UsesOnlyConsole), false)]
    [InlineData("System.IO.FileStream", typeof(UsesNeitherForbidden), false)]
    [InlineData("System.IO.FileInfo", typeof(UsesOnlyFile), false)]
    [InlineData("System.IO.FileInfo", typeof(UsesOnlyDirectory), false)]
    [InlineData("System.IO.FileInfo", typeof(UsesOnlyFileStream), false)]
    [InlineData("System.IO.FileInfo", typeof(UsesOnlyFileInfo), true)]
    [InlineData("System.IO.FileInfo", typeof(UsesOnlyDirectoryInfo), false)]
    [InlineData("System.IO.FileInfo", typeof(UsesOnlyFileSystemInfo), false)]
    [InlineData("System.IO.FileInfo", typeof(UsesOnlyConsole), false)]
    [InlineData("System.IO.FileInfo", typeof(UsesNeitherForbidden), false)]
    [InlineData("System.IO.DirectoryInfo", typeof(UsesOnlyFile), false)]
    [InlineData("System.IO.DirectoryInfo", typeof(UsesOnlyDirectory), false)]
    [InlineData("System.IO.DirectoryInfo", typeof(UsesOnlyFileStream), false)]
    [InlineData("System.IO.DirectoryInfo", typeof(UsesOnlyFileInfo), false)]
    [InlineData("System.IO.DirectoryInfo", typeof(UsesOnlyDirectoryInfo), true)]
    [InlineData("System.IO.DirectoryInfo", typeof(UsesOnlyFileSystemInfo), false)]
    [InlineData("System.IO.DirectoryInfo", typeof(UsesOnlyConsole), false)]
    [InlineData("System.IO.DirectoryInfo", typeof(UsesNeitherForbidden), false)]
    [InlineData("System.IO.FileSystemInfo", typeof(UsesOnlyFile), false)]
    [InlineData("System.IO.FileSystemInfo", typeof(UsesOnlyDirectory), false)]
    [InlineData("System.IO.FileSystemInfo", typeof(UsesOnlyFileStream), false)]
    [InlineData("System.IO.FileSystemInfo", typeof(UsesOnlyFileInfo), false)]
    [InlineData("System.IO.FileSystemInfo", typeof(UsesOnlyDirectoryInfo), false)]
    [InlineData("System.IO.FileSystemInfo", typeof(UsesOnlyFileSystemInfo), true)]
    [InlineData("System.IO.FileSystemInfo", typeof(UsesOnlyConsole), false)]
    [InlineData("System.IO.FileSystemInfo", typeof(UsesNeitherForbidden), false)]
    [InlineData("System.Console", typeof(UsesOnlyFile), false)]
    [InlineData("System.Console", typeof(UsesOnlyDirectory), false)]
    [InlineData("System.Console", typeof(UsesOnlyFileStream), false)]
    [InlineData("System.Console", typeof(UsesOnlyFileInfo), false)]
    [InlineData("System.Console", typeof(UsesOnlyDirectoryInfo), false)]
    [InlineData("System.Console", typeof(UsesOnlyFileSystemInfo), false)]
    [InlineData("System.Console", typeof(UsesOnlyConsole), true)]
    [InlineData("System.Console", typeof(UsesNeitherForbidden), false)]
    public void ForbiddenTypeRule_Discriminates(string forbidden, Type type, bool expectFlagged) =>
        Assert.Equal(expectFlagged, Flags(type, forbidden));

    // D-29: NetArchTest 1.3.2 matches whole type names, not prefixes, so ConsoleColor is not caught by "System.Console"
    // (the spec names only System.Console). This pins that behaviour so an upgrade that changes it is noticed.
    [Fact]
    public void ForbiddenTypeRule_ConsoleColorIsNotConsole() => Assert.False(Flags(typeof(UsesOnlyConsoleColor), "System.Console"));

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

/// <summary>Sanity cases for the App rules (§3.2 App row, §2 BCL-only, R42), through the production helpers (D-37).</summary>
public class AppRuleSanityTests
{
    private static readonly System.Reflection.Assembly Self = typeof(AppRuleSanityTests).Assembly;

    private static TestResult AppRuleOn(Type t) =>
        Types.InAssembly(Self).That().HaveName(t.Name).ShouldNot().HaveDependencyOnAny([.. AppForbidden]).GetResult();

    [Fact]
    public void AppCliRule_FailsOnKnownViolation()
    {
        var r = AppRuleOn(typeof(UsesCli));
        Assert.False(r.IsSuccessful, "a dependency on Gridline.Cli was not detected");
        Assert.Contains(typeof(UsesCli).FullName!, r.FailingTypeNames!);
    }

    [Fact]
    public void AppCliRule_AllowsCoreIoAndWpf() => Assert.True(AppRuleOn(typeof(UsesCoreIoAndWpf)).IsSuccessful);

    [Fact]
    public void AppReferenceRule_FlagsACliReference()
    {
        // This test assembly references Gridline.Cli (and Gridline.App), which App's reference rule must reject.
        var bad = DisallowedGridlineReferences(Self, AppAllowedGridline);
        Assert.Contains(CliAsm.GetName().Name!, bad); // the Cli assembly is named "gridline" (§2)
        Assert.Equal("gridline", CliAsm.GetName().Name);
        Assert.DoesNotContain("Gridline.Core", bad);
        Assert.DoesNotContain("Gridline.IO", bad);
    }

    [Fact]
    public void BclRule_FlagsPackagesAndWpfOutsideApp()
    {
        // The test assembly references xunit and NetArchTest (packages): flagged with or without the WPF allowance.
        Assert.Contains(NonBclReferences(Self, allowWpf: true), s => s.Contains("NetArchTest.Rules"));
        Assert.Contains(NonBclReferences(Self, allowWpf: true), s => s.Contains("xunit"));
        // It also references PresentationFramework (fixtures): allowed only with the App allowance.
        Assert.Contains(NonBclReferences(Self, allowWpf: false), s => s.Contains("PresentationFramework"));
        Assert.DoesNotContain(NonBclReferences(Self, allowWpf: true), s => s.Contains("PresentationFramework"));
        Assert.DoesNotContain(NonBclReferences(Self, allowWpf: true), s => s.Contains("System.Runtime "));
    }

    [Fact]
    public void MutableStaticChecker_AllowsR42Identifiers() => Assert.Empty(FindPublicMutableStatics([typeof(R42Identifiers)]));

    [Fact]
    public void MutableStaticChecker_FlagsGenuineMutableWpfStatics()
    {
        var found = FindPublicMutableStatics([typeof(BadWpfStatics)]);
        Assert.Contains(found, s => s.Contains("NotReadonlyProperty") && s.Contains("not readonly"));
        Assert.Contains(found, s => s.Contains("SharedBrush"));
        Assert.Contains(found, s => s.Contains("SharedPenProp"));
        Assert.Contains(found, s => s.Contains("Registry"));
        Assert.Contains(found, s => s.Contains("SettableCommand") && s.Contains("settable"));
        Assert.Contains(found, s => s.Contains("PlainRoutedCommand"));
        Assert.Equal(7, found.Count); // SettableCommand is reported twice: settable, and of mutable type
    }
}
