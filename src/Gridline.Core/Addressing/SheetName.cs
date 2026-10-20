namespace Gridline.Core.Addressing;

/// <summary>
/// Sheet name rules (CR1.1) and the quoting of a sheet prefix in a qualified reference (CR1.2, R82). Uniqueness among a
/// workbook's sheets is the workbook's job; this type only checks a single name.
/// </summary>
public static class SheetName
{
    /// <summary>The maximum length of a sheet name in UTF-16 characters (CR1.1).</summary>
    public const int MaxLength = 31;

    /// <summary>
    /// True when <paramref name="name"/> is a valid sheet name (CR1.1): 1–31 characters, none of <c>: \ / ? * [ ]</c>,
    /// and not beginning or ending with <c>'</c>.
    /// </summary>
    public static bool IsValid(string? name) => name is not null && IsValid(name.AsSpan());

    internal static bool IsValid(ReadOnlySpan<char> name)
    {
        if (name.Length is < 1 or > MaxLength) return false;
        if (name[0] == '\'' || name[^1] == '\'') return false;
        return name.IndexOfAny(@":\/?*[]") < 0;
    }

    /// <summary>Checks <paramref name="name"/> against the CR1.1 rules.</summary>
    /// <param name="name">The sheet name.</param>
    /// <param name="paramName">The parameter name reported by the exception.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid sheet name.</exception>
    public static void Validate(string name, string paramName)
    {
        ArgumentNullException.ThrowIfNull(name, paramName);
        if (!IsValid(name))
            throw new ArgumentException(
                $"Invalid sheet name '{name}': it must have 1 to {MaxLength} characters, none of : \\ / ? * [ ], and must not begin or end with '.",
                paramName);
    }

    /// <summary>
    /// True when the sheet prefix must be quoted (CR1.2): the name contains a character outside <c>[A-Za-z0-9_.]</c>,
    /// starts with a digit, looks like a cell reference (<c>^[A-Za-z]{1,3}[0-9]+$</c>), or is TRUE or FALSE in any casing.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public static bool RequiresQuoting(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return RequiresQuoting(name.AsSpan());
    }

    internal static bool RequiresQuoting(ReadOnlySpan<char> name)
    {
        if (name.Length == 0) return true;
        foreach (char c in name)
        {
            if (!IsBareChar(c)) return true;
        }
        if (IsDigit(name[0])) return true;
        if (LooksLikeCellReference(name)) return true;
        return name.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || name.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The sheet prefix as printed (CR1.2, §5.5): <paramref name="name"/> unchanged when it needs no quotes, otherwise
    /// wrapped in <c>'</c> with each <c>'</c> doubled (<c>O'Brien</c> → <c>'O''Brien'</c>). The <c>!</c> is not included.
    /// Casing is the caller's: pass the sheet's defined casing.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid sheet name.</exception>
    public static string Format(string name)
    {
        Validate(name, nameof(name));
        return RequiresQuoting(name) ? "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'" : name;
    }

    /// <summary>
    /// Splits an API address string (R83) into its optional sheet prefix and the rest after the <c>!</c>, with the R82
    /// prefix rules: a quoted prefix <c>'…'</c> uses <c>''</c> for <c>'</c> and must hold a valid sheet name; an unquoted
    /// prefix must be a valid name that needs no quoting; <c>!</c> follows the prefix immediately. Text without a prefix
    /// (no leading <c>'</c> and no <c>!</c>) gives a null <paramref name="sheet"/> and <paramref name="rest"/> = the text.
    /// The rest is not checked. False for a malformed prefix.
    /// </summary>
    /// <param name="sheet">The sheet name with quotes removed and <c>''</c> collapsed, or null when unqualified.</param>
    internal static bool TrySplitQualified(string text, out string? sheet, out string rest)
    {
        sheet = null;
        rest = text;
        if (text.Length > 0 && text[0] == '\'')
        {
            var name = new System.Text.StringBuilder();
            int i = 1;
            while (true)
            {
                if (i >= text.Length) return false; // unterminated quote
                if (text[i] == '\'')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\'') { name.Append('\''); i += 2; continue; }
                    break; // the closing quote
                }
                name.Append(text[i++]);
            }
            if (i + 1 >= text.Length || text[i + 1] != '!' || !IsValid(name.ToString())) return false;
            sheet = name.ToString();
            rest = text[(i + 2)..];
            return true;
        }
        int bang = text.IndexOf('!');
        if (bang < 0) return true;
        var prefix = text.AsSpan(0, bang);
        if (!IsValid(prefix) || RequiresQuoting(prefix)) return false;
        sheet = prefix.ToString();
        rest = text[(bang + 1)..];
        return true;
    }

    /// <summary>A character an unquoted sheet prefix may contain: <c>[A-Za-z0-9_.]</c>.</summary>
    internal static bool IsBareChar(char c) => IsAsciiLetter(c) || IsDigit(c) || c is '_' or '.';

    /// <summary><c>^[A-Za-z]{1,3}[0-9]+$</c>, the CR1.2 "looks like a cell reference" test (not limited to the grid).</summary>
    private static bool LooksLikeCellReference(ReadOnlySpan<char> s)
    {
        int i = 0;
        while (i < s.Length && IsAsciiLetter(s[i])) i++;
        if (i is < 1 or > 3 || i == s.Length) return false;
        for (int j = i; j < s.Length; j++)
        {
            if (!IsDigit(s[j])) return false;
        }
        return true;
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';
    private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}
