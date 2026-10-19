using System.Reflection;
using System.Xml.Linq;
using Gridline.Core;
using NetArchTest.Rules;

namespace Gridline.Architecture.Tests;

/// <summary>Shared helpers for the §3.2 / §3.3 architecture rules.</summary>
internal static class ArchSupport
{
    public static Assembly CoreAsm => typeof(Workbook).Assembly;
    public static Assembly IOAsm => typeof(Gridline.IO.GridlineFile).Assembly;
    public static Assembly CliAsm => typeof(Gridline.Cli.ExitCodes).Assembly;
    public static Assembly[] AllSrc => [CoreAsm, IOAsm, CliAsm];

    public const string CoreRoot = "Gridline.Core";
    public static string Ns(string name) => CoreRoot + "." + name;

    /// <summary>Full names of the types that sit directly in the Gridline.Core root namespace
    /// (excluding compiler-generated ones). Used instead of the "Gridline.Core" prefix, which would
    /// match every sub-namespace.</summary>
    public static string[] CoreRootTypeNames() =>
        CoreAsm.GetTypes()
            .Where(t => t.Namespace == CoreRoot && !t.IsNested && !t.Name.StartsWith('<'))
            .Select(t => t.FullName!)
            .ToArray();

    public static string Describe(TestResult r) =>
        r.IsSuccessful ? "" : "Violating types: " + string.Join(", ", r.FailingTypeNames ?? []);

    /// <summary>No type in the given namespace may depend on any of the given namespace/type prefixes.</summary>
    public static TestResult NamespaceMustNotDependOn(Assembly asm, string ns, params string[] forbidden) =>
        Types.InAssembly(asm).That().ResideInNamespaceStartingWith(ns)
            .ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

    public static TestResult AssemblyMustNotDependOn(Assembly asm, params string[] forbidden) =>
        Types.InAssembly(asm).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Gridline.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Gridline.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Project names referenced via ProjectReference in src/{project}/{project}.csproj.</summary>
    public static string[] ProjectReferences(string project)
    {
        var path = Path.Combine(RepoRoot(), "src", project, project + ".csproj");
        return XDocument.Load(path).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension((string)e.Attribute("Include")!))
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();
    }

    public static string[] PackageReferences(string project)
    {
        var path = Path.Combine(RepoRoot(), "src", project, project + ".csproj");
        return XDocument.Load(path).Descendants("PackageReference")
            .Select(e => (string)e.Attribute("Include")!).ToArray();
    }

    public static string[] ReferencedGridlineAssemblies(Assembly asm) =>
        asm.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("Gridline", StringComparison.Ordinal))
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();

    // ---- §3.3: no public mutable static state ----

    private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public static bool IsMutableType(Type t)
    {
        if (t.IsArray) return true;
        if (t == typeof(System.Text.StringBuilder)) return true;
        var ns = t.Namespace ?? "";
        if (!(ns == "System.Collections" || ns.StartsWith("System.Collections.", StringComparison.Ordinal))) return false;
        if (ns.StartsWith("System.Collections.Immutable", StringComparison.Ordinal) || ns.StartsWith("System.Collections.Frozen", StringComparison.Ordinal)) return false;
        if (t.IsInterface && t.Name.StartsWith("IReadOnly", StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Returns a description of every public/protected static mutable-state member on the given types.</summary>
    public static List<string> FindPublicMutableStatics(IEnumerable<Type> types)
    {
        var found = new List<string>();
        foreach (var t in types)
        {
            if (!(t.IsPublic || t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamORAssem)) continue;
            foreach (var f in t.GetFields(Statics))
            {
                bool exposed = f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly;
                if (!exposed || f.IsLiteral) continue; // const
                if (!f.IsInitOnly) found.Add($"{t.FullName}.{f.Name}: static field is not readonly/const");
                else if (IsMutableType(f.FieldType)) found.Add($"{t.FullName}.{f.Name}: static readonly field of mutable type {f.FieldType.Name}");
            }
            foreach (var p in t.GetProperties(Statics))
            {
                var set = p.SetMethod;
                if (set == null || !set.IsStatic) continue;
                bool isInit = set.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
                if (isInit) continue;
                if (set.IsPublic || set.IsFamily || set.IsFamilyOrAssembly) found.Add($"{t.FullName}.{p.Name}: static property has a settable accessor");
            }
        }
        return found;
    }
}
