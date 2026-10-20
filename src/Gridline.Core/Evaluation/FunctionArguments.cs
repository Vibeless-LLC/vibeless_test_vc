using System.Runtime.CompilerServices;
using Gridline.Core.Addressing;
using Gridline.Core.Parsing;

namespace Gridline.Core.Evaluation;

/// <summary>The evaluator's <see cref="IFunctionArguments"/>: wraps the call's argument nodes without exposing them.</summary>
internal sealed class FunctionArguments(EvaluationContext context, IReadOnlyList<FormulaNode> nodes) : IFunctionArguments
{
    private bool _valid = true;

    public CellAddress? CurrentCell => context.CurrentCell;

    public int Count { get { EnsureValid(); return nodes.Count; } }

    public bool IsOmitted(int index) => Node(index) is OmittedArgNode;

    /// <summary>A cell reference, a range, or a defined name with a live target (§7.0, ruling P2). An undefined name or
    /// one whose target was deleted is a direct argument that evaluates to its error (#NAME? / #REF!), so aggregation
    /// propagates it.</summary>
    public bool IsReference(int index) => Unwrap(Node(index)) switch
    {
        CellRefNode or RangeNode => true,
        NameNode n => NameError(n, out _) is null,
        _ => false,
    };

    /// <summary>§9.6, §5.1: an undefined name gives #NAME? and a deleted target #REF!, also where a reference is required.</summary>
    public bool TryGetReferenceError(int index, out CellValue error)
    {
        if (Unwrap(Node(index)) is NameNode n && NameError(n, out _) is { } e)
        {
            error = e;
            return true;
        }
        error = default;
        return false;
    }

    /// <summary>Resolves a name argument: null when it has a live target (<paramref name="target"/>), otherwise its
    /// error (#NAME? when undefined, #REF! when the target was deleted).</summary>
    private CellValue? NameError(NameNode n, out NameTarget target)
    {
        if (!context.TryResolveName(n.Name, out target)) return Errors.Name;
        return target.Kind == NameTargetKind.Invalid ? Errors.Ref : null;
    }

    /// <summary>
    /// Re-entry point. An omitted argument is #VALUE! (§5.3, R38): a function with a default for that position checks
    /// <see cref="IsOmitted"/> first. Backstop (R13b): if the thread stack is nearly exhausted, return #NUM! instead of
    /// recursing.
    /// </summary>
    public CellValue Evaluate(int index)
    {
        var node = Node(index);
        if (node is OmittedArgNode) return Errors.Value;
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) return Errors.Num;
        return context.EvaluateScalar(node);
    }

    public ReferenceValues EnumerateReferenceValues(int index) => Unwrap(Node(index)) switch
    {
        CellRefNode r => new ReferenceValues(context.Cells, new CellRange(r.Reference.Address, r.Reference.Address)),
        RangeNode r => new ReferenceValues(context.Cells, new CellRange(r.Start.Address, r.End.Address)),
        NameNode n when NameError(n, out var t) is null => new ReferenceValues(context.Cells, t.Range),
        _ => throw new InvalidOperationException($"Argument {index} is not a reference."),
    };

    internal void Invalidate() => _valid = false;

    private FormulaNode Node(int index)
    {
        EnsureValid();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, nodes.Count);
        return nodes[index];
    }

    /// <summary>R12: parentheses around a reference keep it a reference.</summary>
    private static FormulaNode Unwrap(FormulaNode node)
    {
        while (node is ParenNode p) node = p.Inner;
        return node;
    }

    private void EnsureValid()
    {
        if (!_valid) throw new InvalidOperationException("Function arguments are only valid during IFormulaFunction.Invoke.");
    }
}
