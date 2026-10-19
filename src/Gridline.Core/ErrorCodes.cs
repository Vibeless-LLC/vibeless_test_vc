using System.Collections.Frozen;

namespace Gridline.Core;

/// <summary>The fixed set of spreadsheet error codes (§9.1).</summary>
public static class ErrorCodes
{
    public const string DivZero = "#DIV/0!";
    public const string NA = "#N/A";
    public const string Name = "#NAME?";
    public const string Num = "#NUM!";
    public const string Ref = "#REF!";
    public const string Value = "#VALUE!";
    public const string Circ = "#CIRC!";
    public const string Parse = "#PARSE!";

    private static readonly FrozenSet<string> Known =
        new[] { DivZero, NA, Name, Num, Ref, Value, Circ, Parse }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>True when <paramref name="code"/> is exactly one of the defined error codes.</summary>
    public static bool IsKnown(string? code) => code is not null && Known.Contains(code);
}
