using Gridline.Core.Parsing;

namespace Gridline.Core.Evaluation;

/// <summary>
/// Scalar evaluator for parsed formulas (§6). Walks the AST with explicit stacks (ruling R9), so long operator chains
/// use constant call-stack depth; the only re-entry is a function evaluating one of its lazy arguments, which is
/// bounded by the parser's 64-level call-nesting cap (R13, §5.4, §6.7), with a stack check at the re-entry point as a
/// backstop (see <see cref="FunctionArguments.Evaluate"/>).
/// </summary>
public static class FormulaEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="formula"/> against <paramref name="cells"/>. A failed parse gives #PARSE! (§5.4);
    /// unknown functions and undefined names give #NAME? (§5.1, §9.6); a Blank final result, e.g. <c>=A1</c> with A1
    /// empty, gives the number 0 (§6.6). A name that refers to a cell behaves like that cell reference, one that
    /// refers to a range like that range (#VALUE! in scalar context, §6.5), and one whose target was deleted gives
    /// #REF! (§9.6). User-level problems are returned as error values; exceptions only come from null arguments or
    /// from a provider or function that throws.
    /// </summary>
    /// <param name="functions">Function lookup; null means no functions (every call is #NAME?).</param>
    /// <param name="names">Name lookup; null means no names are defined (every name is #NAME?).</param>
    public static CellValue Evaluate(ParsedFormula formula, ICellValueProvider cells, IFunctionResolver? functions = null,
        INameResolver? names = null)
    {
        ArgumentNullException.ThrowIfNull(formula);
        ArgumentNullException.ThrowIfNull(cells);
        if (!formula.Success) return Errors.Parse;
        var value = new EvaluationContext(cells, functions, names).EvaluateScalar(formula.Root!);
        return value.Kind == ValueKind.Blank ? CellValue.FromNumber(0) : value;
    }
}

/// <summary>Pre-built error values (immutable).</summary>
internal static class Errors
{
    public static readonly CellValue Value = CellValue.FromError(ErrorCodes.Value);
    public static readonly CellValue Name = CellValue.FromError(ErrorCodes.Name);
    public static readonly CellValue Num = CellValue.FromError(ErrorCodes.Num);
    public static readonly CellValue Div0 = CellValue.FromError(ErrorCodes.Div0);
    public static readonly CellValue Parse = CellValue.FromError(ErrorCodes.Parse);
    public static readonly CellValue Ref = CellValue.FromError(ErrorCodes.Ref);
}

/// <summary>One evaluation's working state: explicit work and value stacks shared by nested argument evaluations.</summary>
internal sealed class EvaluationContext(ICellValueProvider cells, IFunctionResolver? functions, INameResolver? names = null)
{
    private struct Frame
    {
        public FormulaNode Node;
        public bool Expanded; // operands already scheduled; next visit applies the operator
    }

    private Frame[] _work = new Frame[16];
    private int _workCount;
    private CellValue[] _values = new CellValue[16];
    private int _valueCount;

    internal ICellValueProvider Cells { get; } = cells;

    /// <summary>Resolves a name (§9.6); false when no resolver is present or the name is undefined.</summary>
    internal bool TryResolveName(string name, out NameTarget target)
    {
        if (names is not null) return names.TryResolveName(name, out target);
        target = default;
        return false;
    }

    /// <summary>A name in scalar context: undefined → #NAME?, deleted target → #REF!, a cell → its value, a range →
    /// #VALUE! (§6.5, even 1x1, as for <see cref="RangeNode"/>).</summary>
    private CellValue NameValue(string name)
    {
        if (!TryResolveName(name, out var target)) return Errors.Name;
        return target.Kind switch
        {
            NameTargetKind.Cell => Cells.GetValue(target.Range.TopLeft),
            NameTargetKind.Range => Errors.Value,
            _ => Errors.Ref,
        };
    }

    /// <summary>
    /// Evaluates <paramref name="root"/> to a scalar. Re-entrant: a function invoked from here may call it again for
    /// an argument; each call works above the stack heights it found and restores them on exit (even on exceptions).
    /// The result may be Blank (empty cell reference, Blank function result).
    /// </summary>
    public CellValue EvaluateScalar(FormulaNode root)
    {
        int workBase = _workCount, valueBase = _valueCount;
        try
        {
            PushWork(root, false);
            while (_workCount > workBase)
            {
                var frame = _work[--_workCount];
                _work[_workCount] = default; // drop the node reference
                if (frame.Expanded) Apply(frame.Node);
                else if (frame.Node is FunctionCallNode call) PushValue(Invoke(call));
                else Visit(frame.Node);
            }
            var result = _values[--_valueCount];
            _values[_valueCount] = default; // drop the stale reference
            return result;
        }
        finally
        {
            Array.Clear(_work, workBase, _workCount - workBase);
            _workCount = workBase;
            Array.Clear(_values, valueBase, _valueCount - valueBase);
            _valueCount = valueBase;
        }
    }

    private void Visit(FormulaNode node)
    {
        switch (node)
        {
            case NumberNode n: PushValue(CellValue.FromNumber(n.Value)); break;
            case StringNode s: PushValue(CellValue.FromText(s.Value)); break;
            case BooleanNode b: PushValue(CellValue.FromBoolean(b.Value)); break;
            case ErrorNode e: PushValue(CellValue.FromError(e.Code)); break;
            case CellRefNode r: PushValue(Cells.GetValue(r.Reference.Address)); break;
            case RangeNode: PushValue(Errors.Value); break;          // §6.5, R11: even 1x1
            case NameNode n: PushValue(NameValue(n.Name)); break;    // §9.6
            case OmittedArgNode: PushValue(Errors.Value); break;     // §5.3, R38 (normally caught by FunctionArguments)
            case ParenNode p: PushWork(p.Inner, false); break;
            case UnaryNode u: PushWork(u, true); PushWork(u.Operand, false); break;
            case PercentNode p: PushWork(p, true); PushWork(p.Operand, false); break;
            case BinaryNode b:
                PushWork(b, true);
                PushWork(b.Right, false);
                PushWork(b.Left, false); // popped first: left before right (§6.4)
                break;
            default: throw new InvalidOperationException($"Unknown node type {node.GetType().Name}.");
        }
    }

    private void Apply(FormulaNode node)
    {
        switch (node)
        {
            case BinaryNode b:
            {
                var right = _values[--_valueCount];
                var left = _values[_valueCount - 1];
                _values[_valueCount - 1] = Operators.Binary(b.Operator, left, right);
                _values[_valueCount] = default;
                break;
            }
            case UnaryNode u:
                _values[_valueCount - 1] = Operators.Unary(u.Operator, _values[_valueCount - 1]);
                break;
            case PercentNode:
                _values[_valueCount - 1] = Operators.Percent(_values[_valueCount - 1]);
                break;
            default: throw new InvalidOperationException($"Node type {node.GetType().Name} has no operator.");
        }
    }

    private CellValue Invoke(FunctionCallNode call)
    {
        if (functions is null || !functions.TryResolve(call.Name, out var function)) return Errors.Name;
        var arguments = new FunctionArguments(this, call.Arguments);
        CellValue result;
        try { result = function.Invoke(arguments); }
        finally { arguments.Invalidate(); }
        if (result.Kind == ValueKind.Text && result.Text.Length > Coercion.MaxTextLength) return Errors.Value;
        return result;
    }

    private void PushWork(FormulaNode node, bool expanded)
    {
        if (_workCount == _work.Length) Array.Resize(ref _work, _work.Length * 2);
        _work[_workCount++] = new Frame { Node = node, Expanded = expanded };
    }

    private void PushValue(CellValue value)
    {
        if (_valueCount == _values.Length) Array.Resize(ref _values, _values.Length * 2);
        _values[_valueCount++] = value;
    }
}
