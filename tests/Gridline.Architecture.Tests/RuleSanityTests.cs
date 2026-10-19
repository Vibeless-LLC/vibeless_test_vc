using Gridline.Architecture.Tests.Violations;
using NetArchTest.Rules;
using static Gridline.Architecture.Tests.ArchSupport;

namespace Gridline.Architecture.Tests;

/// <summary>Each rule mechanism must fail on a known violation (§3.2, §3.3).</summary>
public class RuleSanityTests
{
    private static readonly System.Reflection.Assembly Self = typeof(RuleSanityTests).Assembly;

    [Fact]
    public void MutableStaticChecker_FlagsKnownViolations()
    {
        var found = FindPublicMutableStatics([typeof(BadStatics)]);
        Assert.Contains(found, s => s.Contains("Counter"));
        Assert.Contains(found, s => s.Contains(".Array"));
        Assert.Contains(found, s => s.Contains(".List"));
        Assert.Contains(found, s => s.Contains("Setter"));
        Assert.DoesNotContain(found, s => s.Contains("InitOnly") || s.Contains("Constant") || s.Contains("ReadonlyString") || s.Contains("Immutable"));
        Assert.Equal(4, found.Count);
    }

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

    [Fact]
    public void NamespaceRule_FailsOnKnownViolation()
    {
        // Same helper and same Ns()-style names as the production §3.3 tests.
        var bad = SiblingRule(Self, typeof(UsesParsing).Namespace!, "Parsing");
        Assert.False(bad.IsSuccessful, "dependency on Gridline.Core.Parsing was not detected");
        var ok = SiblingRule(Self, typeof(UsesParsing).Namespace!, "Commands");
        Assert.True(ok.IsSuccessful, "unrelated sibling must not be flagged: " + Describe(ok));
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
