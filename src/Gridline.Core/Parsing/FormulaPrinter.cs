using System.Text;

namespace Gridline.Core.Parsing;

/// <summary>
/// Canonical formula printer (§5.5): '=' prefix, no whitespace outside string literals, upper-case function names,
/// booleans, error literals and references (each keeping its own $ markers), names in their defined casing (or as
/// written), number lexemes exactly as written, strings re-escaped with "", and parentheses exactly as in the AST.
/// </summary>
/// <remarks>
/// Iterative: a work stack of nodes and literal text replaces recursion, so a left spine of ~16,000 operators (D-13)
/// uses constant call-stack depth (§6.7, R9, R10). A formula that failed to parse has no AST; callers keep its text.
/// </remarks>
public static class FormulaPrinter
{
    /// <summary>Prints <paramref name="root"/> in canonical form, including the leading '='.</summary>
    /// <param name="root">The AST root of a successfully parsed formula (<see cref="ParsedFormula.Root"/>).</param>
    /// <param name="rewriter">Optional reference mapping; null prints every reference unchanged.</param>
    /// <param name="names">Optional name casing; null prints every name as written.</param>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The resolver returned a casing that is not the same name.</exception>
    public static string Print(FormulaNode root, IReferenceRewriter? rewriter = null, INameCasingResolver? names = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var sb = new StringBuilder("=");
        // Each item is either a node still to print or literal text; pushed in reverse so pops come out left to right.
        var work = new Stack<(FormulaNode? Node, string? Text)>();
        work.Push((root, null));
        while (work.Count > 0)
        {
            var (node, text) = work.Pop();
            if (node is null) { sb.Append(text); continue; }
            switch (node)
            {
                case NumberNode n: sb.Append(n.Lexeme); break;
                case StringNode s: sb.Append('"').Append(s.Value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"'); break;
                case BooleanNode b: sb.Append(b.Value ? "TRUE" : "FALSE"); break;
                case ErrorNode e: sb.Append(e.Code); break;
                case CellRefNode c: AppendReference(sb, c, rewriter); break;
                case RangeNode r: AppendRange(sb, r, rewriter); break;
                case NameNode nm: sb.Append(ResolveName(nm.Name, names)); break;
                case OmittedArgNode: break;
                case UnaryNode u:
                    work.Push((u.Operand, null));
                    sb.Append(u.Operator == UnaryOperator.Negate ? '-' : '+');
                    break;
                case PercentNode p:
                    work.Push((null, "%"));
                    work.Push((p.Operand, null));
                    break;
                case BinaryNode bin:
                    work.Push((bin.Right, null));
                    work.Push((null, OperatorText(bin.Operator)));
                    work.Push((bin.Left, null));
                    break;
                case ParenNode paren:
                    work.Push((null, ")"));
                    work.Push((paren.Inner, null));
                    sb.Append('(');
                    break;
                case FunctionCallNode f:
                    work.Push((null, ")"));
                    for (int i = f.Arguments.Count - 1; i >= 0; i--)
                    {
                        work.Push((f.Arguments[i], null));
                        if (i > 0) work.Push((null, ","));
                    }
                    sb.Append(f.Name.ToUpperInvariant()).Append('(');
                    break;
                default:
                    throw new InvalidOperationException($"Unknown formula node {node.GetType().Name}.");
            }
        }
        return sb.ToString();
    }

    private static void AppendReference(StringBuilder sb, CellRefNode c, IReferenceRewriter? rewriter)
    {
        var r = rewriter is null ? c.Reference : rewriter.Rewrite(c.Reference);
        sb.Append(r is { } valid ? valid.ToString() : ErrorCodes.Ref);
    }

    private static void AppendRange(StringBuilder sb, RangeNode r, IReferenceRewriter? rewriter)
    {
        var corners = rewriter is null ? (r.Start, r.End) : rewriter.RewriteRange(r.Start, r.End);
        if (corners is not { } c) { sb.Append(ErrorCodes.Ref); return; } // §5.5: either corner invalid -> whole range #REF!
        sb.Append(c.Start.ToString()).Append(':').Append(c.End.ToString());
    }

    private static string ResolveName(string written, INameCasingResolver? names)
    {
        var defined = names?.GetDefinedCasing(written);
        if (defined is null) return written;
        if (!string.Equals(defined, written, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Name casing '{defined}' does not match name '{written}'.");
        return defined;
    }

    private static string OperatorText(BinaryOperator op) => op switch
    {
        BinaryOperator.Add => "+",
        BinaryOperator.Subtract => "-",
        BinaryOperator.Multiply => "*",
        BinaryOperator.Divide => "/",
        BinaryOperator.Power => "^",
        BinaryOperator.Concat => "&",
        BinaryOperator.Equal => "=",
        BinaryOperator.NotEqual => "<>",
        BinaryOperator.Less => "<",
        BinaryOperator.Greater => ">",
        BinaryOperator.LessEqual => "<=",
        BinaryOperator.GreaterEqual => ">=",
        _ => throw new InvalidOperationException($"Unknown operator {op}."),
    };
}
