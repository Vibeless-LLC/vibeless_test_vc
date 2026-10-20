using static Gridline.Architecture.Tests.ArchSupport;

namespace Gridline.Architecture.Tests;

/// <summary>§3.2 project dependency rules (DECISIONS D-29, D-37).</summary>
public class ProjectRuleTests
{
    // §3.2: Core MUST NOT reference any other Gridline project.
    [Fact]
    public void S3_2_Core_ReferencesNoOtherGridlineProject()
    {
        Assert.Empty(ProjectReferences("Gridline.Core"));
        Assert.Empty(PackageReferences("Gridline.Core"));
        Assert.Empty(ReferencedGridlineAssemblies(CoreAsm));
        var r = AssemblyMustNotDependOn(CoreAsm, "Gridline.IO", "Gridline.Cli", "Gridline.App");
        Assert.True(r.IsSuccessful, Describe(r));
    }

    // §3.2: Core MUST NOT use System.Windows*.
    [Fact]
    public void S3_2_Core_DoesNotDependOnSystemWindows()
    {
        var r = AssemblyMustNotDependOn(CoreAsm, "System.Windows");
        Assert.True(r.IsSuccessful, Describe(r));
    }

    // §3.2: Core MUST NOT use System.IO.File / Directory / FileStream (type-level, not using directives).
    // NetArchTest matches whole type names, not prefixes (D-29), so FileInfo, DirectoryInfo and FileSystemInfo are
    // listed explicitly (ruling R37, stricter than the four types §3.2 names).
    [Theory]
    [InlineData("System.IO.File")]
    [InlineData("System.IO.Directory")]
    [InlineData("System.IO.FileStream")]
    [InlineData("System.IO.FileInfo")]
    [InlineData("System.IO.DirectoryInfo")]
    [InlineData("System.IO.FileSystemInfo")]
    public void S3_2_Core_DoesNotUseForbiddenIoType(string forbidden)
    {
        var r = AssemblyMustNotDependOn(CoreAsm, forbidden);
        Assert.True(r.IsSuccessful, forbidden + ": " + Describe(r));
    }

    // §3.2: Core MUST NOT use System.Console.
    [Fact]
    public void S3_2_Core_DoesNotDependOnSystemConsole()
    {
        var r = AssemblyMustNotDependOn(CoreAsm, "System.Console");
        Assert.True(r.IsSuccessful, Describe(r));
    }

    // §3.2: IO may reference only Core.
    [Fact]
    public void S3_2_IO_ReferencesOnlyCore()
    {
        Assert.Equal(["Gridline.Core"], ProjectReferences("Gridline.IO"));
        Assert.Empty(PackageReferences("Gridline.IO"));
        Assert.All(ReferencedGridlineAssemblies(IOAsm), n => Assert.Equal("Gridline.Core", n));
    }

    // §3.2: IO MUST NOT depend on App, Cli, System.Windows* or System.Console.
    [Fact]
    public void S3_2_IO_DoesNotDependOnAppCliWindowsOrConsole()
    {
        var r = AssemblyMustNotDependOn(IOAsm, "Gridline.App", "Gridline.Cli", "System.Windows", "System.Console");
        Assert.True(r.IsSuccessful, Describe(r));
    }

    // §3.2: Cli may reference Core and IO.
    [Fact]
    public void S3_2_Cli_ReferencesOnlyCoreAndIO()
    {
        Assert.Equal(["Gridline.Core", "Gridline.IO"], ProjectReferences("Gridline.Cli"));
        Assert.Empty(PackageReferences("Gridline.Cli"));
        Assert.All(ReferencedGridlineAssemblies(CliAsm), n => Assert.Contains(n, new[] { "Gridline.Core", "Gridline.IO" }));
    }

    // §3.2: Cli MUST NOT depend on App or System.Windows*.
    [Fact]
    public void S3_2_Cli_DoesNotDependOnAppOrWindows()
    {
        var r = AssemblyMustNotDependOn(CliAsm, "Gridline.App", "System.Windows");
        Assert.True(r.IsSuccessful, Describe(r));
    }

    // §3.2: App may reference Core and IO.
    [Fact]
    public void S3_2_App_ReferencesOnlyCoreAndIO()
    {
        Assert.Equal(["Gridline.Core", "Gridline.IO"], ProjectReferences("Gridline.App"));
        Assert.Empty(PackageReferences("Gridline.App"));
        Assert.Empty(DisallowedGridlineReferences(AppAsm, AppAllowedGridline));
    }

    // §3.2: App MUST NOT depend on Cli.
    [Fact]
    public void S3_2_App_DoesNotDependOnCli()
    {
        var r = AppMustNotDependOnCli(AppAsm);
        Assert.True(r.IsSuccessful, Describe(r));
    }

    // §2 (D-37): Core, IO and Cli reference only Gridline assemblies and the BCL (Microsoft.NETCore.App); App may also
    // reference the WPF assemblies of Microsoft.WindowsDesktop.App, and nothing else (no packages, no WinForms).
    [Fact]
    public void S2_SrcAssemblies_ReferenceOnlyBclAndGridline()
    {
        foreach (var asm in new[] { CoreAsm, IOAsm, CliAsm })
            Assert.Empty(NonBclReferences(asm, allowWpf: false));
        Assert.Empty(NonBclReferences(AppAsm, allowWpf: true));
    }

    // Non-vacuous: App really references WPF, which the non-App rule would reject.
    [Fact]
    public void S2_App_UsesWpf_WhichOnlyTheAppRuleAllows() =>
        Assert.Contains(NonBclReferences(AppAsm, allowWpf: false), s => s.Contains("PresentationFramework"));
}
