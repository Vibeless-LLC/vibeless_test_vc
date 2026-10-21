using Gridline.Core.Parsing;

namespace Gridline.Core.Commands;

/// <summary>
/// RenameSheet's AST rewrite (CR1.3, §5.5, D-54): the tree that parsing the §5.5 re-emission of a formula yields
/// (<see cref="SheetRewriter"/> + <see cref="FormulaPrinter"/> with the same resolvers), built without printing and
/// re-parsing it (P-8). It applies exactly the printer's canonicalisations that the parser keeps in the tree: a sheet
/// prefix matching <c>oldSheet</c> case-insensitively becomes <c>newSheet</c>; every other sheet prefix and every name
/// take their defined casing (as written when undefined); function names are upper-cased. Everything else the printer
/// emits (references, number lexemes, strings, operators, parentheses) parses back to the same nodes, so those are
/// shared, as are unchanged subtrees. The installed tree therefore equals parse(new input), and later re-emissions
/// depend only on the stored input.
/// Iterative (§6.7, R10): a ~16,000-deep operator chain uses constant call-stack depth.
/// </summary>
internal static class SheetRenamer
{
    /// <param name="names">The name casing the re-emission was printed with.</param>
    /// <param name="sheets">The sheet casing the re-emission was printed with (it must give <paramref name="newSheet"/> its
    /// new casing).</param>
    public static FormulaNode Rename(FormulaNode root, string oldSheet, string newSheet, INameCasingResolver? names,
        ISheetCasingResolver? sheets)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(oldSheet);
        ArgumentNullException.ThrowIfNull(newSheet);
        string? Sheet(string? written) =>
            written is null ? null : Matches(written, oldSheet) ? newSheet : sheets?.GetDefinedCasing(written) ?? written;
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
                    {
                        string? sheet = Sheet(c.Sheet);
                        done.Push(string.Equals(sheet, c.Sheet, StringComparison.Ordinal) ? c : new CellRefNode(c.Reference, sheet));
                        break;
                    }
                    case RangeNode r:
                    {
                        string? sheet = Sheet(r.Sheet);
                        done.Push(string.Equals(sheet, r.Sheet, StringComparison.Ordinal) ? r : new RangeNode(r.Start, r.End, sheet));
                        break;
                    }
                    case NameNode nm:
                    {
                        string name = names?.GetDefinedCasing(nm.Name) ?? nm.Name;
                        done.Push(string.Equals(name, nm.Name, StringComparison.Ordinal) ? nm : new NameNode(name));
                        break;
                    }
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
                    default: done.Push(node); break; // literals and omitted arguments print and parse back unchanged
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
                    string upper = f.Name.ToUpperInvariant(); // the printer upper-cases function names
                    changed |= !string.Equals(upper, f.Name, StringComparison.Ordinal);
                    done.Push(changed ? new FunctionCallNode(upper, args) : f);
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

    private static bool Matches(string sheet, string oldSheet) => sheet.Equals(oldSheet, StringComparison.OrdinalIgnoreCase);
}
