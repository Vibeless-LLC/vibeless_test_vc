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

// §3.2 App row: App MUST NOT depend on Cli. (ExitCodes members are consts and would be inlined, so typeof is used.)
public class UsesCli { public Type P() => typeof(Gridline.Cli.ExitCodes); }
// Uses only what App may use: Core, IO and WPF.
public class UsesCoreIoAndWpf
{
    public object P() => (new Gridline.Core.Workbook(), typeof(Gridline.IO.GridlineFile), new System.Windows.Controls.TextBox());
}

// R42 (§3.3): WPF identifier registrations are allowed ...
public class R42Identifiers : System.Windows.DependencyObject
{
    public static readonly System.Windows.DependencyProperty ValueProperty =
        System.Windows.DependencyProperty.Register("Value", typeof(int), typeof(R42Identifiers));
    public static readonly System.Windows.RoutedEvent PokedEvent =
        System.Windows.EventManager.RegisterRoutedEvent("Poked", System.Windows.RoutingStrategy.Bubble, typeof(System.Windows.RoutedEventHandler), typeof(R42Identifiers));
    public static readonly System.Windows.Input.RoutedUICommand PokeCommand = new("Poke", "Poke", typeof(R42Identifiers));
}

// ... but genuine mutable WPF statics are not.
public class BadWpfStatics
{
    public static System.Windows.DependencyProperty NotReadonlyProperty =
        System.Windows.DependencyProperty.Register("NotReadonly", typeof(int), typeof(BadWpfStatics));
    public static readonly System.Windows.Media.SolidColorBrush SharedBrush = new();
    public static System.Windows.Media.Pen SharedPenProp { get; } = new();
    public static readonly List<System.Windows.DependencyProperty> Registry = [];
    public static System.Windows.Input.RoutedUICommand SettableCommand { get; set; } = new();
    public static readonly System.Windows.Input.RoutedCommand PlainRoutedCommand = new(); // not one of the three R42 types
}
