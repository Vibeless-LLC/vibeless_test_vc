using System.Collections.ObjectModel;
using Gridline.Core.Addressing;

namespace Gridline.Core.Parsing;

/// <summary>Binary operators of the formula grammar (§5.2, §5.3).</summary>
public enum BinaryOperator
{
    Add, Subtract, Multiply, Divide, Power, Concat,
    Equal, NotEqual, Less, Greater, LessEqual, GreaterEqual,
}

/// <summary>Prefix unary operators (§5.2).</summary>
public enum UnaryOperator { Plus, Negate }

/// <summary>Base of the immutable formula AST (§5.3). Consumed by evaluation, dependency extraction and §5.5 printing.</summary>
public abstract class FormulaNode
{
    private protected FormulaNode() { }
}

/// <summary>Number literal; <see cref="Lexeme"/> is the exact source text (kept for §5.5 printing).</summary>
public sealed class NumberNode(double value, string lexeme) : FormulaNode
{
    public double Value { get; } = value;
    public string Lexeme { get; } = lexeme ?? throw new ArgumentNullException(nameof(lexeme));
}

/// <summary>String literal; <see cref="Value"/> is unescaped (no surrounding quotes, "" collapsed).</summary>
public sealed class StringNode(string value) : FormulaNode
{
    public string Value { get; } = value ?? throw new ArgumentNullException(nameof(value));
}

/// <summary>TRUE / FALSE literal.</summary>
public sealed class BooleanNode(bool value) : FormulaNode
{
    public bool Value { get; } = value;
}

/// <summary>Error literal; <see cref="Code"/> is the canonical ErrorCodes value.</summary>
public sealed class ErrorNode(string code) : FormulaNode
{
    public string Code { get; } = code ?? throw new ArgumentNullException(nameof(code));
}

/// <summary>Single cell reference, keeping the $ flags as written, optionally qualified with a sheet (CR1.2).</summary>
public sealed class CellRefNode(CellReference reference, string? sheet = null) : FormulaNode
{
    public CellReference Reference { get; } = reference;

    /// <summary>The sheet name as written (quotes removed, <c>''</c> collapsed), or null for an unqualified reference.</summary>
    public string? Sheet { get; } = ValidSheet(sheet);

    /// <exception cref="ArgumentException">The name breaks the CR1.1 rules.</exception>
    internal static string? ValidSheet(string? sheet)
    {
        if (sheet is not null) SheetName.Validate(sheet, nameof(sheet));
        return sheet;
    }
}

/// <summary>CellRef ':' CellRef with both ends kept as written (not normalised; CellRange normalises at evaluation).
/// A qualified range has one sheet for both corners (CR1.2).</summary>
public sealed class RangeNode(CellReference start, CellReference end, string? sheet = null) : FormulaNode
{
    public CellReference Start { get; } = start;
    public CellReference End { get; } = end;

    /// <summary>The sheet name as written (quotes removed, <c>''</c> collapsed), or null for an unqualified range.</summary>
    public string? Sheet { get; } = CellRefNode.ValidSheet(sheet);
}

/// <summary>A bare name (case as written); resolved later.</summary>
public sealed class NameNode(string name) : FormulaNode
{
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));
}

/// <summary>An omitted function argument, e.g. the middle argument of IF(A1,,3). Counts toward arity.</summary>
public sealed class OmittedArgNode : FormulaNode
{
}

/// <summary>Function call; <see cref="Name"/> is upper-cased; <see cref="Arguments"/> may contain <see cref="OmittedArgNode"/>.</summary>
public sealed class FunctionCallNode : FormulaNode
{
    public FunctionCallNode(string name, IEnumerable<FormulaNode> arguments)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(arguments);
        var args = arguments.ToArray();
        foreach (var a in args) ArgumentNullException.ThrowIfNull(a, nameof(arguments));
        Name = name;
        Arguments = new ReadOnlyCollection<FormulaNode>(args);
    }

    public string Name { get; }
    public IReadOnlyList<FormulaNode> Arguments { get; }
}

/// <summary>Prefix + or -.</summary>
public sealed class UnaryNode(UnaryOperator op, FormulaNode operand) : FormulaNode
{
    public UnaryOperator Operator { get; } = op;
    public FormulaNode Operand { get; } = operand ?? throw new ArgumentNullException(nameof(operand));
}

/// <summary>Postfix %.</summary>
public sealed class PercentNode(FormulaNode operand) : FormulaNode
{
    public FormulaNode Operand { get; } = operand ?? throw new ArgumentNullException(nameof(operand));
}

/// <summary>Infix binary operation.</summary>
public sealed class BinaryNode(BinaryOperator op, FormulaNode left, FormulaNode right) : FormulaNode
{
    public BinaryOperator Operator { get; } = op;
    public FormulaNode Left { get; } = left ?? throw new ArgumentNullException(nameof(left));
    public FormulaNode Right { get; } = right ?? throw new ArgumentNullException(nameof(right));
}

/// <summary>An explicit parenthesised group, kept so §5.5 printing can reproduce it.</summary>
public sealed class ParenNode(FormulaNode inner) : FormulaNode
{
    public FormulaNode Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));
}
