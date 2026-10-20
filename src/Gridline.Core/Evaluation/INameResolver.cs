using Gridline.Core.Addressing;

namespace Gridline.Core.Evaluation;

/// <summary>What a defined name refers to (§9.6): a single cell, a range, or a deleted target (<c>#REF!</c>, ruling P1).</summary>
public enum NameTargetKind
{
    /// <summary>A single cell such as <c>$B$1</c>; behaves like that cell reference.</summary>
    Cell,

    /// <summary>A range such as <c>$A$1:$C$10</c> (also <c>$A$1:$A$1</c>, R58); behaves like that range.</summary>
    Range,

    /// <summary>The target was deleted (§9.6, R59); the name evaluates to #REF!.</summary>
    Invalid,
}

/// <summary>The resolved target of a defined name. <see cref="Range"/> is normalised (a cell is a 1x1 range) and is
/// meaningless when <see cref="Kind"/> is <see cref="NameTargetKind.Invalid"/>.</summary>
public readonly record struct NameTarget(NameTargetKind Kind, CellRange Range)
{
    public static NameTarget Invalid => new(NameTargetKind.Invalid, default);
}

/// <summary>
/// Resolves name references during evaluation (§5.1, §9.6), so Evaluation needs no knowledge of the Workbook or of
/// Dependencies (§3.3). Matching is case-insensitive.
/// </summary>
public interface INameResolver
{
    /// <summary>The target of the name as written; false when the name is not defined (it evaluates to #NAME?).</summary>
    bool TryResolveName(string name, out NameTarget target);
}
