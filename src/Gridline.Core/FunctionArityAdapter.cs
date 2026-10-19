using Gridline.Core.Functions;
using Gridline.Core.Parsing;

namespace Gridline.Core;

/// <summary>
/// Feeds the built-in function table's arity data to the parser (rulings R3, R16). Lives in the root namespace so
/// that Functions does not depend on Parsing and Parsing does not depend on Functions (§3.3).
/// </summary>
public sealed class FunctionArityAdapter(BuiltInFunctionTable table) : IFunctionArityProvider
{
    /// <summary>Adapter over <see cref="BuiltInFunctionTable.Default"/>.</summary>
    public FunctionArityAdapter() : this(BuiltInFunctionTable.Default) { }

    public bool TryGetArity(string upperName, out int min, out int max) =>
        table.TryGetArity(upperName, out min, out max);
}
