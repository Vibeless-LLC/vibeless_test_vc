namespace Gridline.Core.Addressing;

/// <summary>A rectangular range (§4.2), always normalised so TopLeft is the minimum column/row.</summary>
public readonly record struct CellRange
{
    public CellAddress TopLeft { get; }
    public CellAddress BottomRight { get; }

    public CellRange(CellAddress first, CellAddress second)
    {
        TopLeft = new CellAddress(Math.Min(first.Column, second.Column), Math.Min(first.Row, second.Row));
        BottomRight = new CellAddress(Math.Max(first.Column, second.Column), Math.Max(first.Row, second.Row));
    }

    public long CellCount =>
        (long)(BottomRight.Column - TopLeft.Column + 1) * (BottomRight.Row - TopLeft.Row + 1);

    public bool Contains(CellAddress a) =>
        a.Column >= TopLeft.Column && a.Column <= BottomRight.Column &&
        a.Row >= TopLeft.Row && a.Row <= BottomRight.Row;

    /// <summary>Parses ADDR:ADDR (each end may carry $), normalising e.g. B3:A1 to A1:B3.</summary>
    public static bool TryParse(string? text, out CellRange range)
    {
        range = default;
        if (text is null) return false;
        var s = text.AsSpan();
        int colon = s.IndexOf(':');
        if (colon < 0) return false;
        if (!CellReference.TryParse(s[..colon], out var a) || !CellReference.TryParse(s[(colon + 1)..], out var b))
            return false;
        range = new CellRange(a.Address, b.Address);
        return true;
    }

    /// <exception cref="ArgumentException">The text is not a valid range.</exception>
    public static CellRange Parse(string text)
        => TryParse(text, out var r) ? r : throw new ArgumentException($"Invalid cell range: '{text}'.", nameof(text));

    /// <summary>Canonical form, e.g. "A1:B3".</summary>
    public override string ToString() => $"{TopLeft}:{BottomRight}";
}
