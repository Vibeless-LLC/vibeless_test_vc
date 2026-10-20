using Gridline.Core.Addressing;

namespace Gridline.Core.Parsing;

/// <summary>
/// Maps the references of a formula while it is printed in canonical form (§5.5). Used by CopyPaste shifting (§9.4) and
/// row/column insert/delete adjustment (§9.5). Returning null means the reference became invalid; it prints as #REF!.
/// </summary>
/// <remarks>
/// The printer calls <see cref="Rewrite"/> for each single cell reference and <see cref="RewriteRange"/> for each
/// <c>A1:B2</c> range, both with the $ flags as written. The default <see cref="RewriteRange"/> rewrites each corner with
/// <see cref="Rewrite"/> and makes the whole range invalid when either corner is (§5.5), which is what §9.4 shifting needs.
/// Override it when the corners must be adjusted together, such as the §9.5 delete rule that keeps the surviving rows.
/// The printer does not track changes; a caller that needs "was any reference changed" (§9.5) records it here.
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
}
