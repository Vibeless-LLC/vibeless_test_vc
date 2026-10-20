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
        List<ResizedReference>? resized = null;

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
                    if (TryResized(call, out var rr))
                    {
                        if (rr.Range.Literal is { } rangeLit && rr.Anchor.Literal is { } anchorLit)
                        {
                            var rect = ResizedReference.Resize(rangeLit, anchorLit); // fixed by the text alone
                            if ((seenRanges ??= []).Add(rect)) (ranges ??= []).Add(rect);
                        }
                        else (resized ??= []).Add(rr);
                    }
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
        return new FormulaPrecedents(cells?.ToArray() ?? [], ranges?.ToArray() ?? [], names?.ToArray() ?? [], resized?.ToArray());
    }

    /// <summary>
    /// R68, §6.1: <c>SUMIF(range, criteria, sum_range)</c> and <c>AVERAGEIF(...)</c> read the rectangle anchored at the
    /// top-left of the third argument with the size of <c>range</c>, which can lie outside the third argument's text. When
    /// both are references (cell, range or name, parentheses allowed) that rectangle is a static dependency. Matched by
    /// function name here, so Dependencies never needs the Functions namespace (§3.3).
    /// </summary>
    private static bool TryResized(FunctionCallNode call, out ResizedReference resized)
    {
        resized = default;
        if (call.Arguments.Count != 3 ||
            !(call.Name.Equals("SUMIF", StringComparison.OrdinalIgnoreCase) ||
              call.Name.Equals("AVERAGEIF", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!TrySpec(call.Arguments[0], out var range) || !TrySpec(call.Arguments[2], out var anchor)) return false;
        resized = new ResizedReference(range, anchor);
        return true;
    }

    private static bool TrySpec(FormulaNode node, out ReferenceSpec spec)
    {
        while (node is ParenNode p) node = p.Inner; // R12: parentheses keep a reference a reference
        switch (node)
        {
            case CellRefNode r: spec = new ReferenceSpec(new CellRange(r.Reference.Address, r.Reference.Address), null); return true;
            case RangeNode r: spec = new ReferenceSpec(new CellRange(r.Start.Address, r.End.Address), null); return true;
            case NameNode n: spec = new ReferenceSpec(null, n.Name.ToUpperInvariant()); return true;
            default: spec = default; return false;
        }
    }
}
