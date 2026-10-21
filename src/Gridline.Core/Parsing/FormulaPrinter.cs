using System.Text;
using Gridline.Core.Addressing;

namespace Gridline.Core.Parsing;

/// <summary>
/// Canonical formula printer (§5.5): '=' prefix, no whitespace outside string literals, upper-case function names,
/// booleans, error literals and references (each keeping its own $ markers), names in their defined casing (or as
/// written), number lexemes exactly as written, strings re-escaped with "", and parentheses exactly as in the AST.
/// A qualified reference (CR1.2, R82) prints its sheet in the defined casing (or as written for an unknown sheet),
/// quoted only when required, then '!' and the reference: <c>'My Sheet'!$A$1:B3</c>.
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
    /// <param name="sheets">Optional sheet casing (R82); null prints every sheet prefix in its casing as written.</param>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A resolver returned a casing that is not the same name or sheet, or the
    /// rewriter returned an invalid sheet name.</exception>
    public static string Print(FormulaNode root, IReferenceRewriter? rewriter = null, INameCasingResolver? names = null,
        ISheetCasingResolver? sheets = null)
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
                case CellRefNode c: AppendReference(sb, c, rewriter, sheets); break;
                case RangeNode r: AppendRange(sb, r, rewriter, sheets); break;
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

    private static void AppendReference(StringBuilder sb, CellRefNode c, IReferenceRewriter? rewriter, ISheetCasingResolver? sheets)
    {
        var r = rewriter is null ? (c.Sheet, c.Reference) : rewriter.Rewrite(c.Sheet, c.Reference);
        if (r is not { } valid) { sb.Append(ErrorCodes.Ref); return; }
        AppendSheet(sb, valid.Sheet, sheets);
        sb.Append(valid.Reference.ToString());
    }

    private static void AppendRange(StringBuilder sb, RangeNode r, IReferenceRewriter? rewriter, ISheetCasingResolver? sheets)
    {
        var range = rewriter is null ? (r.Sheet, r.Start, r.End) : rewriter.RewriteRange(r.Sheet, r.Start, r.End);
        if (range is not { } c) { sb.Append(ErrorCodes.Ref); return; } // §5.5: either corner invalid -> whole range #REF!
        AppendSheet(sb, c.Sheet, sheets);
        sb.Append(c.Start.ToString()).Append(':').Append(c.End.ToString());
    }

    /// <summary>R82: the sheet prefix in its defined casing (as written when unknown), quoted only when required, then '!'.
    /// Appends nothing for an unqualified reference.</summary>
    private static void AppendSheet(StringBuilder sb, string? written, ISheetCasingResolver? sheets)
    {
        if (written is null) return;
        if (!SheetName.IsValid(written))
            throw new InvalidOperationException($"Reference rewriter returned an invalid sheet name '{written}'.");
        var defined = sheets?.GetDefinedCasing(written);
        if (defined is not null && !string.Equals(defined, written, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Sheet casing '{defined}' does not match sheet '{written}'.");
        sb.Append(SheetName.Format(defined ?? written)).Append('!');
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
