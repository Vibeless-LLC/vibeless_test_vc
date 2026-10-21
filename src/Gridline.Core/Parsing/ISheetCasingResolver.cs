namespace Gridline.Core.Parsing;

/// <summary>
/// Supplies the casing of an existing sheet for canonical printing (§5.5 as amended by CR1.2, R82): a qualified reference
/// prints its sheet in the sheet's defined casing, or as written when no such sheet exists. The counterpart of
/// <see cref="INameCasingResolver"/> for sheet prefixes.
/// </summary>
public interface ISheetCasingResolver
{
    /// <param name="sheet">The sheet name as written in the formula (quotes removed, <c>''</c> collapsed).</param>
    /// <returns>The sheet's defined casing (matching <paramref name="sheet"/> case-insensitively), or null when no such sheet exists.</returns>
    string? GetDefinedCasing(string sheet);
}
