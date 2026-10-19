using System.Runtime.CompilerServices;
using Gridline.Core.Addressing;
using Gridline.Core.Parsing;

namespace Gridline.Core.Evaluation;

/// <summary>The evaluator's <see cref="IFunctionArguments"/>: wraps the call's argument nodes without exposing them.</summary>
internal sealed class FunctionArguments(EvaluationContext context, IReadOnlyList<FormulaNode> nodes) : IFunctionArguments
{
    private bool _valid = true;

    public int Count { get { EnsureValid(); return nodes.Count; } }

    public bool IsOmitted(int index) => Node(index) is OmittedArgNode;

    public bool IsReference(int index) => Unwrap(Node(index)) is CellRefNode or RangeNode;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public CellValue Evaluate(int index)
    {
        var node = Node(index);
        return node is OmittedArgNode ? CellValue.Blank : context.EvaluateScalar(node);
    }

    public ReferenceValues EnumerateReferenceValues(int index) => Unwrap(Node(index)) switch
    {
        CellRefNode r => new ReferenceValues(context.Cells, new CellRange(r.Reference.Address, r.Reference.Address)),
        RangeNode r => new ReferenceValues(context.Cells, new CellRange(r.Start.Address, r.End.Address)),
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
