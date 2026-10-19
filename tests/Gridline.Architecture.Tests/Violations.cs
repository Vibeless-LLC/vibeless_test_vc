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
}

public class UsesForbiddenTypes
{
    public bool F() => File.Exists("x");
    public bool D() => Directory.Exists("x");
    public FileStream S() => new("x", FileMode.Open);
    public void C() => Console.WriteLine();
    public object W() => new Gridline.Core.Workbook();
}
