using Gridline.Core.Addressing;
using Gridline.Core.Evaluation;

namespace Gridline.Core;

/// <summary>The §9.6 name and <c>refersTo</c> rules (R58, R59). Pure and allocation-light; ASCII only.</summary>
internal static class NameSyntax
{
    public const int MaxNameLength = 255;

    /// <summary>The <c>refersTo</c> of a name whose target was deleted (§9.6); only the loader and structure edits
    /// may store it (R59, ruling P1).</summary>
    public const string DeletedTarget = ErrorCodes.Ref;

    /// <summary>
    /// §9.6: matches <c>^[A-Za-z_][A-Za-z0-9_.]{0,254}$</c> (checked by hand, so a trailing newline cannot slip through
    /// as it would with a .NET <c>$</c> anchor), is not <c>TRUE</c>/<c>FALSE</c> in any casing, and does not match
    /// <c>^[A-Za-z]{1,3}[0-9]+$</c> (it would look like a reference).
    /// </summary>
    public static bool IsValidName(string name)
    {
        if (name.Length is < 1 or > MaxNameLength) return false;
        if (!(IsLetter(name[0]) || name[0] == '_')) return false;
        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (!(IsLetter(c) || IsDigit(c) || c is '_' or '.')) return false;
        }
        if (name.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || name.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            return false;
        return !LooksLikeReference(name);
    }

    /// <summary><c>^[A-Za-z]{1,3}[0-9]+$</c>.</summary>
    private static bool LooksLikeReference(string name)
    {
        int letters = 0;
        while (letters < name.Length && IsLetter(name[letters])) letters++;
        if (letters is < 1 or > 3 || letters == name.Length) return false;
        for (int i = letters; i < name.Length; i++)
            if (!IsDigit(name[i])) return false;
        return true;
    }

    /// <summary>
    /// Parses a <c>refersTo</c> (§9.6, R58): a fully absolute cell (<c>$B$1</c>) or range (<c>$A$1:$C$10</c>), any
    /// letter casing, every row and column component with <c>$</c>, no whitespace. The canonical form is uppercase
    /// with range corners normalised top-left:bottom-right; <c>$A$1:$A$1</c> stays a range. With
    /// <paramref name="allowDeleted"/> the exact string <c>#REF!</c> is also accepted as a deleted target (R59).
    /// </summary>
    public static bool TryParseRefersTo(string text, bool allowDeleted, out string canonical, out NameTarget target)
    {
        canonical = "";
        target = default;
        if (allowDeleted && string.Equals(text, DeletedTarget, StringComparison.Ordinal))
        {
            canonical = DeletedTarget;
            target = NameTarget.Invalid;
            return true;
        }
        var s = text.AsSpan();
        int colon = s.IndexOf(':');
        if (colon < 0)
        {
            if (!TryParseAbsolute(s, out var cell)) return false;
            canonical = Absolute(cell);
            target = new NameTarget(NameTargetKind.Cell, new CellRange(cell, cell));
            return true;
        }
        if (!TryParseAbsolute(s[..colon], out var a) || !TryParseAbsolute(s[(colon + 1)..], out var b)) return false;
        var range = new CellRange(a, b);
        canonical = Absolute(range.TopLeft) + ":" + Absolute(range.BottomRight);
        target = new NameTarget(NameTargetKind.Range, range);
        return true;
    }

    private static bool TryParseAbsolute(ReadOnlySpan<char> s, out CellAddress address)
    {
        address = default;
        if (!CellReference.TryParse(s, out var r) || !r.ColumnAbsolute || !r.RowAbsolute) return false;
        address = r.Address;
        return true;
    }

    private static string Absolute(CellAddress a) => new CellReference(a, ColumnAbsolute: true, RowAbsolute: true).ToString();

    private static bool IsLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    private static bool IsDigit(char c) => c >= '0' && c <= '9';
}
