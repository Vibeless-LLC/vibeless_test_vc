namespace Gridline.Core.Parsing;

/// <summary>
/// Arity table consulted by the parser (§5.4, ruling R3). Functions-agnostic: Functions exposes its own metadata and an
/// adapter outside Parsing implements this. Unknown names return false and parse successfully.
/// </summary>
public interface IFunctionArityProvider
{
    /// <param name="upperName">The upper-cased function name.</param>
    /// <param name="min">Minimum argument count (omitted arguments count).</param>
    /// <param name="max">Maximum argument count (omitted arguments count).</param>
    bool TryGetArity(string upperName, out int min, out int max);
}
