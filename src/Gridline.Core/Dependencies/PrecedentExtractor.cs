using Gridline.Core.Addressing;
using Gridline.Core.Parsing;

namespace Gridline.Core.Dependencies;

/// <summary>
/// Extracts the static precedents of a formula (§6.1): every cell reference, range and name in the text, including
/// those inside branches that evaluation would not take. Walks the AST with an explicit stack (R9), so a ~16k-deep
/// operator chain uses constant call-stack depth.
/// </summary>
public static class PrecedentExtractor
{
    /// <summary>The precedents of <paramref name="formula"/>; a null or failed parse has none (§5.4).</summary>
    public static FormulaPrecedents Extract(ParsedFormula? formula)
    {
        if (formula?.Root is not { } root) return FormulaPrecedents.Empty;

        List<CellAddress>? cells = null;
        List<CellRange>? ranges = null;
        List<string>? names = null;
        HashSet<CellAddress>? seenCells = null;
        HashSet<CellRange>? seenRanges = null;
        HashSet<string>? seenNames = null; // ordinal set: O(1) dedupe per name instead of a list scan

        var stack = new Stack<FormulaNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case CellRefNode r:
                    if ((seenCells ??= []).Add(r.Reference.Address)) (cells ??= []).Add(r.Reference.Address);
                    break;
                case RangeNode r:
                    var range = new CellRange(r.Start.Address, r.End.Address);
                    if ((seenRanges ??= []).Add(range)) (ranges ??= []).Add(range);
                    break;
                case NameNode n:
                    var upper = n.Name.ToUpperInvariant();
                    if ((seenNames ??= new HashSet<string>(StringComparer.Ordinal)).Add(upper)) (names ??= []).Add(upper);
                    break;
                case FunctionCallNode call:
                    for (int i = call.Arguments.Count - 1; i >= 0; i--) stack.Push(call.Arguments[i]); // left first
                    break;
                case BinaryNode b:
                    stack.Push(b.Right);
                    stack.Push(b.Left);
                    break;
                case UnaryNode u: stack.Push(u.Operand); break;
                case PercentNode p: stack.Push(p.Operand); break;
                case ParenNode p: stack.Push(p.Inner); break;
                // Literals and omitted arguments have no precedents.
            }
        }

        if (cells is null && ranges is null && names is null) return FormulaPrecedents.Empty;
        return new FormulaPrecedents(cells?.ToArray() ?? [], ranges?.ToArray() ?? [], names?.ToArray() ?? []);
    }
}
