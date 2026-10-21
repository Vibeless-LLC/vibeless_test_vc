namespace Gridline.Core.Parsing;

/// <summary>
/// Parser for the §5.3 grammar with §5.2 precedence (tightest first): ':' (CellRef ':' CellRef only), prefix + -,
/// postfix %, ^, * /, + -, &amp;, comparisons; every binary level is left-associative. Errors (§5.4) are returned,
/// never thrown, as a <see cref="ParsedFormula"/> failure, which the engine stores as a formula evaluating to #PARSE!.
/// </summary>
/// <remarks>
/// Implemented as an iterative operator-precedence (shunting-yard) parser with explicit operand, operator and
/// group stacks, so call-stack use is constant regardless of nesting; nesting is still capped by ruling R8.
/// </remarks>
public static class FormulaParser
{
    /// <summary>Maximum nesting of parentheses, function calls and prefix unary operators combined (ruling R8).</summary>
    public const int MaxNestingDepth = 1024;

    /// <summary>Maximum nesting of function calls (ruling R13, §5.4, §6.7): the 65th nested call is a parse error, which bounds
    /// evaluator re-entry through lazy arguments. The combined <see cref="MaxNestingDepth"/> cap still applies.</summary>
    public const int MaxCallNestingDepth = 64;

    private const int ComparisonLevel = 1;
    private const int ConcatLevel = 2;
    private const int AdditiveLevel = 3;
    private const int MultiplicativeLevel = 4;
    private const int PowerLevel = 5;

    /// <param name="formulaText">The formula text after the leading '='.</param>
    /// <param name="arities">Optional arity table; a known function with an out-of-range argument count is a parse error.</param>
    /// <exception cref="ArgumentNullException"><paramref name="formulaText"/> is null.</exception>
    public static ParsedFormula Parse(string formulaText, IFunctionArityProvider? arities)
    {
        ArgumentNullException.ThrowIfNull(formulaText);
        var tokenized = Tokenizer.Tokenize(formulaText);
        if (!tokenized.Success) return ParsedFormula.Fail(tokenized.ErrorMessage, tokenized.ErrorPosition);
        try
        {
            return ParsedFormula.Ok(new Parser(tokenized.Tokens, arities).Run());
        }
        catch (ParseFailure f)
        {
            return ParsedFormula.Fail(f.Message, f.Position);
        }
    }

    /// <summary>Private unwinding signal; always caught in <see cref="Parse"/>.</summary>
    private sealed class ParseFailure(string message, int position) : Exception(message)
    {
        public int Position { get; } = position;
    }

    private readonly record struct PendingOp(bool IsUnary, UnaryOperator Unary, BinaryOperator Binary, int Level);

    /// <summary>An open '(' group or function call. Reductions never cross <see cref="OpBase"/>.</summary>
    private sealed class Group(Token opener, bool isCall, int opBase, int operandBase)
    {
        public Token Opener { get; } = opener;
        public bool IsCall { get; } = isCall;
        public int OpBase { get; } = opBase;
        public int OperandBase { get; } = operandBase;
        public List<FormulaNode> Args { get; } = [];
    }

    private sealed class Parser(IReadOnlyList<Token> tokens, IFunctionArityProvider? arities)
    {
        private readonly List<FormulaNode> _operands = [];
        private readonly List<PendingOp> _ops = [];
        private readonly List<Group> _groups = [];
        private int _pos;
        private int _depth;
        private int _callDepth;

        private Token Current => tokens[_pos];
        private int OpBase => _groups.Count == 0 ? 0 : _groups[^1].OpBase;

        public FormulaNode Run()
        {
            if (Current.Kind == TokenKind.End) throw new ParseFailure("Empty formula.", Current.Position);
            bool expectOperand = true;
            while (true)
            {
                var t = Current;
                if (expectOperand)
                {
                    expectOperand = ReadOperand(t);
                    continue;
                }

                if (t.Kind == TokenKind.Percent)
                {
                    // Prefix unary binds tighter than %, so -50% is (-50)%.
                    ReduceUnary();
                    Push(new PercentNode(Pop()));
                    _pos++;
                }
                else if (TryBinary(t.Kind, out var op, out int level))
                {
                    // Left-associative: reduce pending operators at the same or a tighter level.
                    while (_ops.Count > OpBase && (_ops[^1].IsUnary || _ops[^1].Level >= level)) ReduceOne();
                    _ops.Add(new PendingOp(false, default, op, level));
                    _pos++;
                    expectOperand = true;
                }
                else if (t.Kind == TokenKind.Comma && _groups.Count > 0 && _groups[^1].IsCall)
                {
                    ReduceToBase();
                    _groups[^1].Args.Add(Pop());
                    _pos++;
                    expectOperand = true;
                }
                else if (t.Kind == TokenKind.RParen && _groups.Count > 0)
                {
                    ReduceToBase();
                    CloseGroup(Pop());
                    _pos++;
                }
                else if (t.Kind == TokenKind.End)
                {
                    if (_groups.Count > 0) throw new ParseFailure("Expected ')', found end of formula.", t.Position);
                    ReduceToBase();
                    return Pop();
                }
                else
                {
                    // Covers adjacent operands (5abc, 5A1, 1.5.3, $A$1(1)), stray ')' or ',', and ':' misuse.
                    throw Unexpected(t);
                }
            }
        }

        /// <summary>Consumes one prefix operator, opener or operand; returns true while an operand is still expected.</summary>
        private bool ReadOperand(Token t)
        {
            switch (t.Kind)
            {
                case TokenKind.Plus:
                case TokenKind.Minus:
                    Enter(t);
                    _ops.Add(new PendingOp(true, t.Kind == TokenKind.Minus ? UnaryOperator.Negate : UnaryOperator.Plus, default, 0));
                    _pos++;
                    return true;
                case TokenKind.Number:
                    _pos++;
                    Push(new NumberNode(t.Number, t.Lexeme));
                    return false;
                case TokenKind.String:
                    _pos++;
                    Push(new StringNode(t.Text));
                    return false;
                case TokenKind.Boolean:
                    _pos++;
                    Push(new BooleanNode(t.Boolean));
                    return false;
                case TokenKind.Error:
                    _pos++;
                    Push(new ErrorNode(t.Text));
                    return false;
                case TokenKind.CellRef:
                    Push(ReadReference());
                    return false;
                case TokenKind.Name:
                    _pos++;
                    if (Current.Kind == TokenKind.LParen)
                        throw new ParseFailure($"Name '{t.Lexeme}' cannot be followed by '(' (no space is allowed before a function's '(').", Current.Position);
                    Push(new NameNode(t.Text));
                    return false;
                case TokenKind.LParen:
                    Enter(t);
                    _groups.Add(new Group(t, isCall: false, _ops.Count, _operands.Count));
                    _pos++;
                    return true;
                case TokenKind.Function:
                    Enter(t);
                    if (++_callDepth > MaxCallNestingDepth)
                        throw new ParseFailure($"Function calls are nested deeper than the maximum of {MaxCallNestingDepth}.", t.Position);
                    _pos++;
                    if (Current.Kind != TokenKind.LParen) throw Unexpected(Current); // the tokenizer guarantees '('
                    _pos++;
                    _groups.Add(new Group(t, isCall: true, _ops.Count, _operands.Count));
                    if (Current.Kind != TokenKind.RParen) return true;
                    _pos++;
                    CloseGroup(null); // F() has zero arguments
                    return false;
                case TokenKind.Comma:
                case TokenKind.RParen:
                    if (AtArgumentStart())
                    {
                        // Empty argument (IF(A1,,3), F(1,), F(,)); the ',' or ')' is then handled as after an operand.
                        Push(new OmittedArgNode());
                        return false;
                    }
                    throw Unexpected(t);
                default:
                    throw Unexpected(t);
            }
        }

        // CellRef [':' CellRef] -- ':' is valid only between two single cell references. A range has one sheet prefix,
        // on its first corner (CR1.2, R82): Sheet2!A1:Sheet2!B3 and A1:Data!B2 are errors.
        private FormulaNode ReadReference()
        {
            var start = Current;
            _pos++;
            if (Current.Kind != TokenKind.Colon) return new CellRefNode(start.Reference, start.Sheet);
            _pos++;
            var end = Current;
            if (end.Kind != TokenKind.CellRef)
                throw new ParseFailure($"':' must be followed by a cell reference, found {Describe(end)}.", end.Position);
            if (end.Sheet is not null)
                throw new ParseFailure("A range has a single sheet prefix, before its first cell reference.", end.Position);
            _pos++;
            if (Current.Kind == TokenKind.Colon)
                throw new ParseFailure("A range must have exactly two cell references.", Current.Position);
            return new RangeNode(start.Reference, end.Reference, start.Sheet);
        }

        private bool AtArgumentStart()
        {
            if (_groups.Count == 0) return false;
            var g = _groups[^1];
            return g.IsCall && _ops.Count == g.OpBase && _operands.Count == g.OperandBase;
        }

        /// <param name="last">The final argument or the group's inner expression; null only for a zero-argument call.</param>
        private void CloseGroup(FormulaNode? last)
        {
            var g = _groups[^1];
            _groups.RemoveAt(_groups.Count - 1);
            _depth--;
            if (!g.IsCall)
            {
                Push(new ParenNode(last!));
                return;
            }
            _callDepth--;
            if (last is not null) g.Args.Add(last);
            string name = g.Opener.Text;
            if (arities is not null && arities.TryGetArity(name, out int min, out int max)
                && (g.Args.Count < min || g.Args.Count > max))
            {
                string expected = min == max ? $"{min}" : $"{min} to {max}";
                throw new ParseFailure($"{name} takes {expected} argument(s), got {g.Args.Count}.", g.Opener.Position);
            }
            Push(new FunctionCallNode(name, g.Args));
        }

        private void ReduceUnary()
        {
            while (_ops.Count > OpBase && _ops[^1].IsUnary) ReduceOne();
        }

        private void ReduceToBase()
        {
            while (_ops.Count > OpBase) ReduceOne();
        }

        private void ReduceOne()
        {
            var op = _ops[^1];
            _ops.RemoveAt(_ops.Count - 1);
            if (op.IsUnary)
            {
                Push(new UnaryNode(op.Unary, Pop()));
                _depth--;
            }
            else
            {
                var right = Pop();
                var left = Pop();
                Push(new BinaryNode(op.Binary, left, right));
            }
        }

        private void Enter(Token at)
        {
            if (++_depth > MaxNestingDepth)
                throw new ParseFailure($"Formula nesting exceeds the maximum depth of {MaxNestingDepth}.", at.Position);
        }

        private void Push(FormulaNode node) => _operands.Add(node);

        private FormulaNode Pop()
        {
            var n = _operands[^1];
            _operands.RemoveAt(_operands.Count - 1);
            return n;
        }

        private static ParseFailure Unexpected(Token t) => new($"Unexpected {Describe(t)}.", t.Position);

        private static string Describe(Token t) => t.Kind == TokenKind.End ? "end of formula" : $"'{t.Lexeme}'";

        private static bool TryBinary(TokenKind kind, out BinaryOperator op, out int level)
        {
            (op, level) = kind switch
            {
                TokenKind.Caret => (BinaryOperator.Power, PowerLevel),
                TokenKind.Star => (BinaryOperator.Multiply, MultiplicativeLevel),
                TokenKind.Slash => (BinaryOperator.Divide, MultiplicativeLevel),
                TokenKind.Plus => (BinaryOperator.Add, AdditiveLevel),
                TokenKind.Minus => (BinaryOperator.Subtract, AdditiveLevel),
                TokenKind.Ampersand => (BinaryOperator.Concat, ConcatLevel),
                TokenKind.Equal => (BinaryOperator.Equal, ComparisonLevel),
                TokenKind.NotEqual => (BinaryOperator.NotEqual, ComparisonLevel),
                TokenKind.Less => (BinaryOperator.Less, ComparisonLevel),
                TokenKind.Greater => (BinaryOperator.Greater, ComparisonLevel),
                TokenKind.LessEqual => (BinaryOperator.LessEqual, ComparisonLevel),
                TokenKind.GreaterEqual => (BinaryOperator.GreaterEqual, ComparisonLevel),
                _ => (default, 0),
            };
            return level != 0;
        }
    }
}
