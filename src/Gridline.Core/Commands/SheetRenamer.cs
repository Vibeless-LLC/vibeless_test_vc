using Gridline.Core.Parsing;

namespace Gridline.Core.Commands;

/// <summary>
/// RenameSheet's AST rewrite (CR1.3, D-54): a copy of a formula tree in which every qualified reference, single or range,
/// whose sheet matches <c>oldSheet</c> case-insensitively names <c>newSheet</c> instead. Unchanged subtrees are shared.
/// The result is the tree that parsing the §5.5 re-emission (<see cref="SheetRewriter"/> + <see cref="FormulaPrinter"/>)
/// yields, up to the casing of names as written, which nothing downstream depends on (sheet lookup, precedents and
/// printing are case-insensitive or use the defined casing). It saves re-parsing every rewritten formula (P-8).
/// Iterative (§6.7, R10): a ~16,000-deep operator chain uses constant call-stack depth.
/// </summary>
internal static class SheetRenamer
{
    public static FormulaNode Rename(FormulaNode root, string oldSheet, string newSheet)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(oldSheet);
        ArgumentNullException.ThrowIfNull(newSheet);
        var work = new Stack<(FormulaNode Node, bool Children)>();
        var done = new Stack<FormulaNode>(); // rewritten subtrees, left to right
        work.Push((root, false));
        while (work.Count > 0)
        {
            var (node, children) = work.Pop();
            if (!children)
            {
                switch (node)
                {
                    case CellRefNode c:
                        done.Push(Matches(c.Sheet, oldSheet) ? new CellRefNode(c.Reference, newSheet) : c);
                        break;
                    case RangeNode r:
                        done.Push(Matches(r.Sheet, oldSheet) ? new RangeNode(r.Start, r.End, newSheet) : r);
                        break;
                    case FunctionCallNode f:
                        work.Push((f, true));
                        for (int i = f.Arguments.Count - 1; i >= 0; i--) work.Push((f.Arguments[i], false));
                        break;
                    case BinaryNode b:
                        work.Push((b, true));
                        work.Push((b.Right, false));
                        work.Push((b.Left, false));
                        break;
                    case UnaryNode u: work.Push((u, true)); work.Push((u.Operand, false)); break;
                    case PercentNode p: work.Push((p, true)); work.Push((p.Operand, false)); break;
                    case ParenNode g: work.Push((g, true)); work.Push((g.Inner, false)); break;
                    default: done.Push(node); break; // literals, names, omitted arguments
                }
                continue;
            }
            switch (node)
            {
                case FunctionCallNode f:
                {
                    var args = new FormulaNode[f.Arguments.Count];
                    bool changed = false;
                    for (int i = args.Length - 1; i >= 0; i--)
                    {
                        args[i] = done.Pop();
                        changed |= !ReferenceEquals(args[i], f.Arguments[i]);
                    }
                    done.Push(changed ? new FunctionCallNode(f.Name, args) : f);
                    break;
                }
                case BinaryNode b:
                {
                    var right = done.Pop();
                    var left = done.Pop();
                    done.Push(ReferenceEquals(left, b.Left) && ReferenceEquals(right, b.Right) ? b : new BinaryNode(b.Operator, left, right));
                    break;
                }
                case UnaryNode u:
                {
                    var operand = done.Pop();
                    done.Push(ReferenceEquals(operand, u.Operand) ? u : new UnaryNode(u.Operator, operand));
                    break;
                }
                case PercentNode p:
                {
                    var operand = done.Pop();
                    done.Push(ReferenceEquals(operand, p.Operand) ? p : new PercentNode(operand));
                    break;
                }
                case ParenNode g:
                {
                    var inner = done.Pop();
                    done.Push(ReferenceEquals(inner, g.Inner) ? g : new ParenNode(inner));
                    break;
                }
            }
        }
        return done.Pop();
    }

    private static bool Matches(string? sheet, string oldSheet) =>
        sheet is not null && sheet.Equals(oldSheet, StringComparison.OrdinalIgnoreCase);
}
