namespace Gridline.Core.Parsing;

/// <summary>
/// Supplies the casing of a defined name for canonical printing (§5.5, §9.6): a name reference prints with the casing of
/// its definition, or as written when the name is undefined.
/// </summary>
public interface INameCasingResolver
{
    /// <param name="name">The name as written in the formula.</param>
    /// <returns>The defined name's casing (matching <paramref name="name"/> case-insensitively), or null when it is undefined.</returns>
    string? GetDefinedCasing(string name);
}
