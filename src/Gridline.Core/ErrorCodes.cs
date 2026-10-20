using System.Collections.Frozen;

namespace Gridline.Core;

/// <summary>The fixed set of spreadsheet error codes (§9.1).</summary>
public static class ErrorCodes
{
    public const string Div0 = "#DIV/0!";
    public const string Value = "#VALUE!";
    public const string Ref = "#REF!";
    public const string Name = "#NAME?";
    public const string NA = "#N/A";
    public const string Num = "#NUM!";
    public const string Circ = "#CIRC!";
    public const string Parse = "#PARSE!";

    private static readonly string[] All = [Div0, Value, Ref, Name, NA, Num, Circ, Parse];

    private static readonly FrozenSet<string> Known = All.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> KnownSpans = Known.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly FrozenDictionary<string, string> CanonicalIgnoreCase =
        All.ToFrozenDictionary(c => c, c => c, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> CanonicalSpans =
        CanonicalIgnoreCase.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Length of the longest code (<c>#DIV/0!</c>, <c>#PARSE!</c>, <c>#VALUE!</c>: 7).</summary>
    internal static readonly int MaxLength = All.Max(c => c.Length);

    /// <summary>Span form of <see cref="IsKnown(string?)"/>; allocation-free.</summary>
    internal static bool IsKnown(ReadOnlySpan<char> code) => code.Length <= MaxLength && KnownSpans.Contains(code);

    /// <summary>R56 (§5.1, supersedes R52): matches <paramref name="code"/> against the eight codes ignoring ASCII case and
    /// returns the canonical upper-case code (<c>#n/a</c> → <c>#N/A</c>). Allocation-free.</summary>
    internal static bool TryGetCanonical(ReadOnlySpan<char> code, out string canonical)
    {
        if (code.Length <= MaxLength && CanonicalSpans.TryGetValue(code, out string? found))
        {
            canonical = found;
            return true;
        }
        canonical = "";
        return false;
    }

    /// <summary>True when <paramref name="code"/> is exactly one of the defined error codes.</summary>
    public static bool IsKnown(string? code) => code is not null && Known.Contains(code);
}
