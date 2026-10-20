namespace Gridline.Architecture.Tests.Violations;

// Deliberate violations used only by RuleSanityTests to prove the rules are not vacuous.
public static class BadStatics
{
    public static int Counter;
    public static readonly int[] Array = [1];
    public static readonly List<int> List = [];
    public static string Setter { get; set; } = "";
    public static string InitOnly { get; } = "";
    public const int Constant = 1;
    public static readonly string ReadonlyString = "ok";
    public static readonly System.Collections.Immutable.ImmutableArray<int> Immutable = [];

    // §3.3 extensions: get-only properties of a mutable type, and static events.
    public static int[] GetOnlyArray { get; } = [1];
    public static List<int> GetOnlyList { get; } = [];
    public static event EventHandler? Changed;
    public static void Raise() => Changed?.Invoke(null, EventArgs.Empty);
    // Allowed: read-only wrapper types.
    public static System.Collections.ObjectModel.ReadOnlyCollection<int> ReadOnlyWrapper { get; } = new([1]);
    public static IReadOnlyList<int> ReadOnlyInterface { get; } = [1];
    public static System.Collections.Frozen.FrozenSet<int> FrozenSetProp { get; } = System.Collections.Frozen.FrozenSet.ToFrozenSet(new[] { 1 });
    public static System.Collections.Frozen.FrozenDictionary<int, int> FrozenDictionaryProp { get; } = System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new Dictionary<int, int>());
    public static System.Collections.Immutable.ImmutableArray<int> ImmutableArrayProp { get; } = [1];
    public static IEnumerable<int> EnumerableProp { get; } = [1];
    public static IReadOnlyDictionary<int, int> ReadOnlyDictionaryProp { get; } = new Dictionary<int, int>();
    public static IReadOnlyCollection<int> ReadOnlyCollectionProp { get; } = [1];
    public static IComparer<int> ComparerIfaceProp { get; } = Comparer<int>.Default;
    public static IEqualityComparer<int> EqualityComparerIfaceProp { get; } = EqualityComparer<int>.Default;
    public static Comparer<int> ComparerProp { get; } = Comparer<int>.Default;
    public static EqualityComparer<int> EqualityComparerProp { get; } = EqualityComparer<int>.Default;
    public static KeyValuePair<int, int> PairProp { get; } = new(1, 2);
}

// Protected statics are exposed to derived classes of a public type.
public class BadProtectedStatics
{
    protected static int Counter;
    protected static readonly List<int> Items = [];
    protected static string Setter { get; set; } = "";
    protected static event EventHandler? Changed;
    protected static void Raise() => Changed?.Invoke(null, EventArgs.Empty);
    // Allowed.
    protected static readonly string Fine = "ok";
    protected const int Constant = 1;
}

// Instance init-only properties are not static state (C# has no static init accessors); non-public statics are not exposed.
public class GoodInstanceAndPrivate
{
    public List<int> InitOnly { get; init; } = [];
    public string InitName { get; init; } = "";
    private static int _hidden = 1;
    internal static int Internal = 2;
    private static readonly List<int> HiddenList = [];
    public static int Peek() => _hidden + Internal + HiddenList.Count;
}

public class UsesForbiddenTypes
{
    public bool F() => File.Exists("x");
    public bool D() => Directory.Exists("x");
    public FileStream S() => new("x", FileMode.Open);
    public void C() => Console.WriteLine();
    public object W() => new Gridline.Core.Workbook();
}

// One type per forbidden name so a rule that matches too much or too little is caught.
public class UsesOnlyFile { public bool F() => File.Exists("x"); }
public class UsesOnlyDirectory { public bool D() => Directory.Exists("x"); }
public class UsesOnlyFileStream { public FileStream S() => new("x", FileMode.Open); }
public class UsesOnlyConsole { public void C() => Console.WriteLine(); }
// Related BCL types, banned by their own explicit names (ruling R37, DECISIONS D-29).
public class UsesOnlyFileInfo { public long L() => new FileInfo("x").Length; }
public class UsesOnlyDirectoryInfo { public void E() => new DirectoryInfo("x").Create(); }
public class UsesOnlyFileSystemInfo { public bool E(FileSystemInfo i) => i.Exists; }
public class UsesOnlyConsoleColor { public ConsoleColor C() => ConsoleColor.Red; }
// Uses none of File, Directory, FileStream or Console (Path and StringBuilder are other System.IO / System types).
public class UsesNeitherForbidden { public string P() => Path.Combine("a", "b") + new System.Text.StringBuilder().Append('x'); }
