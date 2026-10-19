using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Gridline.Core.Evaluation;

namespace Gridline.Core.Functions;

/// <summary>
/// The immutable table of built-in functions (§6.8, §7; ruling R16): name to arity range and implementation. It is the
/// <see cref="IFunctionResolver"/> the evaluator uses, and its arity data is fed to the parser by
/// <see cref="Gridline.Core.FunctionArityAdapter"/> (R3), so Functions never touches Parsing.
/// </summary>
/// <remarks>
/// Registration pattern: each function family lives in its own file with an
/// <c>internal static void Register(BuiltInFunctionTable.Builder builder)</c> method (see MathFunctions), and
/// <see cref="Create"/> calls each family's method once. Adding a family is one new file plus one line in
/// <see cref="Create"/>. Names are upper-case; duplicates and bad arity ranges throw at build time.
/// </remarks>
public sealed class BuiltInFunctionTable : IFunctionResolver
{
    /// <summary>Largest argument count a variadic function accepts.</summary>
    public const int MaxVariadicArguments = 255;

    private readonly FrozenDictionary<string, FunctionDefinition> _byName;

    private BuiltInFunctionTable(FunctionDefinition[] definitions)
    {
        Definitions = Array.AsReadOnly(definitions);
        _byName = definitions.ToFrozenDictionary(d => d.Name, StringComparer.Ordinal);
    }

    /// <summary>The shared table of all built-ins; immutable, built once on first use.</summary>
    public static BuiltInFunctionTable Default { get; } = Create();

    /// <summary>All registered functions, in registration order.</summary>
    public IReadOnlyList<FunctionDefinition> Definitions { get; }

    public bool TryResolve(string upperName, [NotNullWhen(true)] out IFormulaFunction? function)
    {
        if (_byName.TryGetValue(upperName, out var definition))
        {
            function = definition.Implementation;
            return true;
        }
        function = null;
        return false;
    }

    /// <summary>The argument count range of <paramref name="upperName"/>; false for an unknown name.</summary>
    public bool TryGetArity(string upperName, out int min, out int max)
    {
        if (_byName.TryGetValue(upperName, out var definition))
        {
            min = definition.MinArguments;
            max = definition.MaxArguments;
            return true;
        }
        min = max = 0;
        return false;
    }

    private static BuiltInFunctionTable Create()
    {
        var builder = new Builder();
        MathFunctions.Register(builder); // §7.1
        LogicFunctions.Register(builder); // §7.2
        // Task 10: TextFunctions.Register(builder);
        return builder.Build();
    }

    /// <summary>Collects definitions while the table is being built.</summary>
    public sealed class Builder
    {
        private readonly List<FunctionDefinition> _definitions = [];
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);

        /// <exception cref="ArgumentException">Name not upper-case, duplicate, or arity range invalid.</exception>
        public void Add(string name, int minArguments, int maxArguments, IFormulaFunction implementation)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(implementation);
            if (name.Length == 0 || name != name.ToUpperInvariant())
                throw new ArgumentException($"Function name '{name}' must be non-empty and upper-case.", nameof(name));
            if (minArguments < 0 || maxArguments < minArguments || maxArguments > MaxVariadicArguments)
                throw new ArgumentException($"Invalid arity {minArguments}..{maxArguments} for {name}.", nameof(maxArguments));
            if (!_names.Add(name))
                throw new ArgumentException($"Function '{name}' is already registered.", nameof(name));
            _definitions.Add(new FunctionDefinition(name, minArguments, maxArguments, implementation));
        }

        public BuiltInFunctionTable Build() => new([.. _definitions]);
    }
}
