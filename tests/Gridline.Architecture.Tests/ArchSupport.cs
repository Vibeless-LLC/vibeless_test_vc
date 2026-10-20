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
    public static Assembly AppAsm => typeof(Gridline.App.App).Assembly;
    public static Assembly[] AllSrc => [CoreAsm, IOAsm, CliAsm, AppAsm];

    // ---- §3.2 App row: may reference Core and IO; MUST NOT depend on Cli ----

    /// <summary>The Gridline assemblies App may reference (§3.2).</summary>
    public static readonly IReadOnlyList<string> AppAllowedGridline = ["Gridline.Core", "Gridline.IO"];

    /// <summary>What App MUST NOT depend on (§3.2), for the type-level rule.</summary>
    public static readonly IReadOnlyList<string> AppForbidden = ["Gridline.Cli"];

    /// <summary>Referenced Gridline assemblies of <paramref name="asm"/> that are not in <paramref name="allowed"/>.</summary>
    public static string[] DisallowedGridlineReferences(Assembly asm, IReadOnlyList<string> allowed) =>
        ReferencedGridlineAssemblies(asm).Where(n => !allowed.Contains(n)).ToArray();

    // ---- §2: src assemblies reference only the BCL (D-37) ----

    /// <summary>The Windows Desktop shared-framework assemblies that make up WPF and its UI Automation peers. Only App may
    /// reference them, and only these: WinForms and the rest of Microsoft.WindowsDesktop.App stay out (§2, D-37).</summary>
    public static readonly IReadOnlyList<string> WpfAssemblies =
        ["PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml", "UIAutomationTypes", "UIAutomationProvider"];

    /// <summary>The shared framework directories an allowed non-Gridline reference must load from.</summary>
    private static readonly string NetCoreAppDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    private static readonly string WindowsDesktopAppDir = Path.GetDirectoryName(typeof(System.Windows.DependencyObject).Assembly.Location)!;

    /// <summary>
    /// Every referenced assembly of <paramref name="asm"/> that is not allowed by §2: a reference is allowed when it is a
    /// Gridline assembly, or when it resolves from the Microsoft.NETCore.App shared framework (the BCL), or, with
    /// <paramref name="allowWpf"/>, when it is one of <see cref="WpfAssemblies"/> and resolves from Microsoft.WindowsDesktop.App.
    /// </summary>
    public static List<string> NonBclReferences(Assembly asm, bool allowWpf)
    {
        var bad = new List<string>();
        foreach (AssemblyName reference in asm.GetReferencedAssemblies())
        {
            string name = reference.Name!;
            if (name.StartsWith("Gridline.", StringComparison.Ordinal)) continue;
            string? dir = TryLoadDirectory(reference);
            bool bcl = dir != null && SamePath(dir, NetCoreAppDir);
            bool wpf = allowWpf && WpfAssemblies.Contains(name) && dir != null && SamePath(dir, WindowsDesktopAppDir);
            if (!bcl && !wpf) bad.Add($"{asm.GetName().Name} references {name} ({dir ?? "unresolved"})");
        }
        return bad;
    }

    private static string? TryLoadDirectory(AssemblyName reference)
    {
        try { return Path.GetDirectoryName(Assembly.Load(reference).Location); }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException) { return null; }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

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

    /// <summary>All Core namespaces that take part in the §3.3 rules.</summary>
    public static readonly IReadOnlyList<string> CoreNamespaces = ["Addressing", "Parsing", "Evaluation", "Functions", "Dependencies", "Commands"];

    /// <summary>The §3.3 rules, owner namespace -> sibling namespaces it MUST NOT depend on. The production tests
    /// and the sanity tests both read this table, so a rule cannot be changed in one place only.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NamespaceRules =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["Addressing"] = ["Parsing", "Evaluation", "Functions", "Dependencies", "Commands"], // plus the Core root types
            ["Parsing"] = ["Evaluation", "Functions", "Dependencies", "Commands"],
            ["Evaluation"] = ["Commands", "Dependencies"],
            ["Functions"] = ["Parsing", "Dependencies", "Commands"],
            ["Dependencies"] = ["Functions", "Commands"],
        };

    public static string Describe(TestResult r) =>
        r.IsSuccessful ? "" : "Violating types: " + string.Join(", ", r.FailingTypeNames ?? []);

    /// <summary>No type in the given namespace may depend on any of the given namespace/type prefixes.</summary>
    public static TestResult NamespaceMustNotDependOn(Assembly asm, string ns, params string[] forbidden) =>
        Types.InAssembly(asm).That().ResideInNamespaceStartingWith(ns)
            .ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

    /// <summary>The single helper behind every §3.3 sibling-namespace rule: types under ownerNamespace must not
    /// depend on Gridline.Core.{sibling}. NOTE: NetArchTest 1.3.2 never matches a namespace with a trailing dot,
    /// so the namespace is passed bare (prefix collisions are excluded by a separate test).</summary>
    public static TestResult SiblingRule(Assembly asm, string ownerNamespace, params string[] siblings) =>
        NamespaceMustNotDependOn(asm, ownerNamespace, siblings.Select(Ns).ToArray());

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
        asm.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("Gridline", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();

    // ---- §3.3: no public mutable static state ----

    private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>Ruling R42 (§3.3): <c>public static readonly</c> DependencyProperty, RoutedEvent and RoutedUICommand
    /// identifier fields are immutable registrations and are allowed. Exact field types only, readonly only.</summary>
    public static bool IsR42Identifier(FieldInfo f) =>
        f.IsStatic && f.IsInitOnly
        && (f.FieldType == typeof(System.Windows.DependencyProperty) || f.FieldType == typeof(System.Windows.RoutedEvent)
            || f.FieldType == typeof(System.Windows.Input.RoutedUICommand));

    public static bool IsMutableType(Type t)
    {
        if (t.IsArray) return true;
        // WPF objects (brushes, pens, elements, any Freezable) carry mutable dependency-property state (D-37).
        if (typeof(System.Windows.DependencyObject).IsAssignableFrom(t)) return true;
        // Routed commands own a mutable InputGestures collection (and RoutedUICommand a settable Text); only the R42
        // readonly identifier fields above are exempt.
        if (typeof(System.Windows.Input.RoutedCommand).IsAssignableFrom(t)) return true;
        if (t == typeof(System.Text.StringBuilder)) return true;
        var ns = t.Namespace ?? "";
        if (!(ns == "System.Collections" || ns.StartsWith("System.Collections.", StringComparison.Ordinal))) return false;
        if (ns.StartsWith("System.Collections.Immutable", StringComparison.Ordinal) || ns.StartsWith("System.Collections.Frozen", StringComparison.Ordinal)) return false;
        if (t.Name.StartsWith("ReadOnly", StringComparison.Ordinal)) return false; // ReadOnlyCollection<T>, ReadOnlyDictionary<,>
        if (t.IsGenericType && ReadOnlyGenerics.Contains(t.GetGenericTypeDefinition())) return false;
        if (t.IsInterface && t.Name.StartsWith("IReadOnly", StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Generic types in System.Collections* that hold no mutable collection state: the read-only views, the
    /// enumerable and comparer abstractions, and the key/value pair struct.</summary>
    private static readonly HashSet<Type> ReadOnlyGenerics =
    [
        typeof(IEnumerable<>), typeof(IReadOnlyList<>), typeof(IReadOnlyCollection<>), typeof(IReadOnlyDictionary<,>), typeof(IReadOnlySet<>),
        typeof(IComparer<>), typeof(IEqualityComparer<>), typeof(Comparer<>), typeof(EqualityComparer<>), typeof(KeyValuePair<,>),
    ];

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
                if (IsR42Identifier(f)) continue;
                if (!f.IsInitOnly) found.Add($"{t.FullName}.{f.Name}: static field is not readonly/const");
                else if (IsMutableType(f.FieldType)) found.Add($"{t.FullName}.{f.Name}: static readonly field of mutable type {f.FieldType.Name}");
            }
            foreach (var p in t.GetProperties(Statics))
            {
                var set = p.SetMethod;
                if (set != null && set.IsStatic)
                {
                    bool isInit = set.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
                    if (!isInit && (set.IsPublic || set.IsFamily || set.IsFamilyOrAssembly)) found.Add($"{t.FullName}.{p.Name}: static property has a settable accessor");
                }
                // A get-only (or init-only) static property of a mutable type still shares mutable state.
                var get = p.GetMethod;
                if (get != null && get.IsStatic && (get.IsPublic || get.IsFamily || get.IsFamilyOrAssembly) && IsMutableType(p.PropertyType))
                    found.Add($"{t.FullName}.{p.Name}: static property exposes mutable type {p.PropertyType.Name}");
            }
            foreach (var e in t.GetEvents(Statics))
            {
                var add = e.AddMethod;
                if (add != null && add.IsStatic && (add.IsPublic || add.IsFamily || add.IsFamilyOrAssembly))
                    found.Add($"{t.FullName}.{e.Name}: static event is mutable shared state");
            }
        }
        return found;
    }
}
