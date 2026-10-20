using System.Collections.Immutable;
using Gridline.Core.Commands;
using Gridline.Core.Evaluation;
using Gridline.Core.Parsing;

namespace Gridline.Core;

// Named ranges (§9.6): the name store, DefineName / DeleteName / GetNames, and the resolvers that expose names to
// evaluation (INameResolver, via RecalcTarget) and to canonical printing (INameCasingResolver, §5.5). Name changes go
// through the shared mutation path in Workbook.Edits.cs as one UndoStep each (§9.3).
public sealed partial class Workbook
{
    /// <summary>One defined name: its definition casing, canonical refersTo and resolved target. The target may be
    /// <see cref="NameTargetKind.Invalid"/> (refersTo <c>#REF!</c>), which only the loader and structure edits create
    /// (ruling P1, R59).</summary>
    private sealed class NameEntry(NameDefinition definition, NameTarget target)
    {
        public readonly NameDefinition Definition = definition;
        public readonly NameTarget Target = target;
        public string Name => Definition.Name;
        public string RefersTo => Definition.RefersTo;
    }

    private readonly Dictionary<string, NameEntry> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Defines <paramref name="name"/> or replaces its definition, keeping the new casing (§9.6); one undo step (§9.3).
    /// Dependents of the name recalculate; a formula that showed #NAME? for it now resolves. Redefining with the same
    /// casing and the same canonical refersTo is a no-op with no step and no event (R60); a casing-only change is a
    /// step. <see cref="CellsChanged"/> carries every address whose value changed and is not raised when none did.
    /// Formula inputs are never rewritten (R60).
    /// </summary>
    /// <param name="refersTo">A fully absolute cell or range such as <c>$B$1</c> or <c>$A$1:$C$10</c>, any casing; stored
    /// uppercase with corners normalised (R58).</param>
    /// <exception cref="ArgumentException">The name breaks a §9.6 syntax rule, or refersTo is not a fully absolute cell
    /// or range (including <c>#REF!</c>, R59); null arguments throw <see cref="ArgumentNullException"/>. Nothing changes.</exception>
    public void DefineName(string name, string refersTo)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(refersTo);
        if (!NameSyntax.IsValidName(name))
            throw new ArgumentException($"Invalid name '{Clip(name)}': a name matches [A-Za-z_][A-Za-z0-9_.]{{0,254}}, is not TRUE or FALSE, and does not look like a cell reference.", nameof(name));
        if (!NameSyntax.TryParseRefersTo(refersTo, allowDeleted: false, out string canonical, out _))
            throw new ArgumentException($"Invalid refersTo '{Clip(refersTo)}': expected a fully absolute cell or range such as $B$1 or $A$1:$C$10.", nameof(refersTo));

        NameDefinition? before = _names.TryGetValue(name, out var current) ? current.Definition : null;
        var after = new NameDefinition(name, canonical);
        if (before == after) // ordinal on both: same casing and same canonical refersTo (R60)
        {
            LastRecalc = default;
            return;
        }
        Perform(new UndoStep([], [new NameEdit(before, after)]));
    }

    /// <summary>Deletes a defined name (§9.6, case-insensitive); one undo step (§9.3). Its dependents recalculate and
    /// show #NAME?; their inputs are not rewritten (R60).</summary>
    /// <exception cref="ArgumentException">No such name is defined (null throws <see cref="ArgumentNullException"/>). Nothing changes.</exception>
    public void DeleteName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_names.TryGetValue(name, out var current))
            throw new ArgumentException($"Name '{Clip(name)}' is not defined.", nameof(name));
        Perform(new UndoStep([], [new NameEdit(current.Definition, null)]));
    }

    /// <summary>Every defined name in its definition casing with its stored refersTo, sorted by name with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> (§9.1, §9.6). A snapshot; later changes do not affect it.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> GetNames()
    {
        var result = new KeyValuePair<string, string>[_names.Count];
        int i = 0;
        foreach (var entry in _names.Values) result[i++] = new(entry.Name, entry.RefersTo);
        Array.Sort(result, (x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.Key, y.Key));
        return Array.AsReadOnly(result);
    }

    /// <summary>Builds the entry for a stored definition. Stored refersTo values are canonical (validated on the way
    /// in), and <c>#REF!</c> is accepted here because the internal model can hold a deleted target (ruling P1).</summary>
    private static NameEntry CreateNameEntry(NameDefinition definition)
    {
        if (!NameSyntax.TryParseRefersTo(definition.RefersTo, allowDeleted: true, out _, out var target))
            throw new InvalidOperationException($"Stored refersTo '{definition.RefersTo}' of name '{definition.Name}' is invalid.");
        return new NameEntry(definition, target);
    }

    /// <summary>Sets the entry of <paramref name="name"/> (matched case-insensitively) to <paramref name="to"/>
    /// (null = undefined) in the store and its target in the dependency graph. Idempotent, so rollback may replay a
    /// name whose forward change threw.</summary>
    private void SetNameEntry(string name, NameEntry? to)
    {
        _names.Remove(name);
        if (to is not null) _names.Add(to.Name, to);
        _graph.SetNameTarget(name, to is { Target.Kind: not NameTargetKind.Invalid } live ? live.Target.Range : null);
    }

    private static string Clip(string s) => s.Length <= 64 ? s : string.Concat(s.AsSpan(0, 64), "...");

    /// <summary>§5.5: names print in their definition casing, or as written when undefined.</summary>
    private sealed class NameCasing(Workbook owner) : INameCasingResolver
    {
        public string? GetDefinedCasing(string name) => owner._names.TryGetValue(name, out var e) ? e.Name : null;
    }
}
