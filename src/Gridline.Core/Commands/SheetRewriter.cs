using Gridline.Core.Addressing;
using Gridline.Core.Parsing;

namespace Gridline.Core.Commands;

/// <summary>
/// The reference rewrite of RenameSheet and DeleteSheet (CR1.3, R85, D-54): a qualified reference, single or range, whose
/// prefix names <c>oldSheet</c> (case-insensitively, R82) gets the prefix <c>newSheet</c>, or becomes invalid
/// (printed <c>#REF!</c>) when <c>newSheet</c> is null. Unqualified references and references to any other sheet,
/// existing or missing, are kept as written.
/// </summary>
internal sealed class SheetRewriter : IReferenceRewriter
{
    private readonly string _oldSheet;
    private readonly string? _newSheet;

    /// <param name="oldSheet">The renamed or deleted sheet.</param>
    /// <param name="newSheet">The new name (a valid CR1.1 name), or null for a deletion.</param>
    public SheetRewriter(string oldSheet, string? newSheet)
    {
        ArgumentNullException.ThrowIfNull(oldSheet);
        _oldSheet = oldSheet;
        _newSheet = newSheet;
    }

    private bool Matches(string? sheet) => sheet is not null && sheet.Equals(_oldSheet, StringComparison.OrdinalIgnoreCase);

    /// <summary>Unqualified references never name a sheet, so they are unchanged.</summary>
    public CellReference? Rewrite(CellReference reference) => reference;

    public (string? Sheet, CellReference Reference)? Rewrite(string? sheet, CellReference reference)
    {
        if (!Matches(sheet)) return (sheet, reference);
        return _newSheet is null ? null : (_newSheet, reference);
    }

    public (string? Sheet, CellReference Start, CellReference End)? RewriteRange(string? sheet, CellReference start, CellReference end)
    {
        if (!Matches(sheet)) return (sheet, start, end);
        return _newSheet is null ? null : (_newSheet, start, end);
    }
}
