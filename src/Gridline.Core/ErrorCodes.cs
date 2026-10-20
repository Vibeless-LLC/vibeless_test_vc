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

    private static readonly string[] All = [DivZero, NA, Name, Num, Ref, Value, Circ, Parse];

    private static readonly FrozenSet<string> Known = All.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> ByAnyCase =
        All.ToFrozenDictionary(c => c, c => c, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="code"/> is exactly one of the defined error codes.</summary>
    public static bool IsKnown(string? code) => code is not null && Known.Contains(code);

    /// <summary>Maps a code in any letter case (e.g. "#div/0!") to its canonical spelling; false when it is not one of the eight codes.</summary>
    internal static bool TryGetCanonical(string? code, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? canonical)
    {
        if (code is null) { canonical = null; return false; }
        return ByAnyCase.TryGetValue(code, out canonical);
    }
}
