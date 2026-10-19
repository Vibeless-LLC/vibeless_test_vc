namespace Gridline.Core.Evaluation;

/// <summary>Ordering used by the comparison operators (§6.3), also available to functions.</summary>
public static class ValueComparer
{
    /// <summary>
    /// Compares two non-error values, returning negative, zero or positive (§6.3). No coercion: values of different
    /// types order Number &lt; Text &lt; Boolean. A Blank adapts to the other side's type (0, "" or FALSE); two blanks
    /// are equal. Numbers compare exactly (IEEE); text compares ordinally after <c>ToUpperInvariant</c>; FALSE &lt; TRUE.
    /// </summary>
    /// <exception cref="ArgumentException">Either value is an error (callers propagate errors first, §6.4).</exception>
    public static int Compare(CellValue left, CellValue right)
    {
        if (left.Kind == ValueKind.Error) throw new ArgumentException("Cannot compare an error.", nameof(left));
        if (right.Kind == ValueKind.Error) throw new ArgumentException("Cannot compare an error.", nameof(right));

        var lk = left.Kind;
        var rk = right.Kind;
        if (lk == ValueKind.Blank && rk == ValueKind.Blank) return 0;
        if (lk == ValueKind.Blank) { left = BlankAs(rk); lk = rk; }
        else if (rk == ValueKind.Blank) { right = BlankAs(lk); rk = lk; }

        if (lk != rk) return Rank(lk).CompareTo(Rank(rk));
        return lk switch
        {
            ValueKind.Number => left.Number.CompareTo(right.Number),
            ValueKind.Boolean => left.Boolean.CompareTo(right.Boolean),
            _ => CompareText(left.Text, right.Text),
        };
    }

    private static int CompareText(string a, string b)
    {
        if (ReferenceEquals(a, b) || string.Equals(a, b, StringComparison.Ordinal)) return 0;
        int c = string.CompareOrdinal(a.ToUpperInvariant(), b.ToUpperInvariant());
        return c < 0 ? -1 : c > 0 ? 1 : 0;
    }

    private static CellValue BlankAs(ValueKind kind) => kind switch
    {
        ValueKind.Number => CellValue.FromNumber(0),
        ValueKind.Text => CellValue.FromText(""),
        _ => CellValue.FromBoolean(false),
    };

    private static int Rank(ValueKind kind) => kind switch
    {
        ValueKind.Number => 0,
        ValueKind.Text => 1,
        _ => 2, // Boolean
    };
}
