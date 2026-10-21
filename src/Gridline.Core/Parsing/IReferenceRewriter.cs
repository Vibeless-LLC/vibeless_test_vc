using Gridline.Core.Addressing;

namespace Gridline.Core.Parsing;

/// <summary>
/// Maps the references of a formula while it is printed in canonical form (§5.5). Used by CopyPaste shifting (§9.4) and
/// row/column insert/delete adjustment (§9.5). Returning null means the reference became invalid; it prints as #REF!.
/// </summary>
/// <remarks>
/// The printer calls the sheet-aware <see cref="Rewrite(string?, CellReference)"/> for each single cell reference and
/// <see cref="RewriteRange(string?, CellReference, CellReference)"/> for each <c>A1:B2</c> range, with the sheet as
/// written (null when unqualified, CR1.2) and the $ flags as written. Their defaults keep the sheet and delegate to
/// <see cref="Rewrite(CellReference)"/> and <see cref="RewriteRange(CellReference, CellReference)"/>, so a rewriter
/// that ignores sheets implements only those. Override the sheet-aware members to act on the sheet: rewrite only the
/// references to one sheet, replace a sheet name (RenameSheet), or return null to print <c>#REF!</c> (DeleteSheet).
/// The default <see cref="RewriteRange(CellReference, CellReference)"/> rewrites each corner with
/// <see cref="Rewrite(CellReference)"/> and makes the whole range invalid when either corner is (§5.5), which is what §9.4
/// shifting needs. Override it when the corners must be adjusted together, such as the §9.5 delete rule that keeps the
/// surviving rows. The printer does not track changes; a caller that needs "was any reference changed" (§9.5) records
/// it here.
/// </remarks>
public interface IReferenceRewriter
{
    /// <summary>The new reference, or null when it became invalid.</summary>
    CellReference? Rewrite(CellReference reference);

    /// <summary>The new corners of a range written <paramref name="start"/>:<paramref name="end"/>, or null when the range became invalid.</summary>
    (CellReference Start, CellReference End)? RewriteRange(CellReference start, CellReference end)
    {
        var s = Rewrite(start);
        if (s is null) return null;
        var e = Rewrite(end);
        if (e is null) return null;
        return (s.Value, e.Value);
    }

    /// <summary>
    /// The new sheet and reference of a single reference written with <paramref name="sheet"/> (null = unqualified), or
    /// null when it became invalid. A returned sheet must be a valid CR1.1 name; null prints the reference unqualified.
    /// </summary>
    (string? Sheet, CellReference Reference)? Rewrite(string? sheet, CellReference reference)
        => Rewrite(reference) is { } r ? (sheet, r) : null;

    /// <summary>
    /// The new sheet and corners of a range written <paramref name="sheet"/>!<paramref name="start"/>:<paramref name="end"/>
    /// (null sheet = unqualified), or null when the range became invalid. A returned sheet must be a valid CR1.1 name.
    /// </summary>
    (string? Sheet, CellReference Start, CellReference End)? RewriteRange(string? sheet, CellReference start, CellReference end)
        => RewriteRange(start, end) is { } c ? (sheet, c.Start, c.End) : null;
}
