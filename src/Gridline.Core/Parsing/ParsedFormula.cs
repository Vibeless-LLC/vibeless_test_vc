namespace Gridline.Core.Parsing;

/// <summary>Outcome of parsing (§5.4): the AST root, or a message and 0-based position (the formula then evaluates to #PARSE!). Never thrown for user input.</summary>
public sealed class ParsedFormula
{
    private ParsedFormula(FormulaNode? root, string errorMessage, int errorPosition)
    {
        Root = root;
        ErrorMessage = errorMessage;
        ErrorPosition = errorPosition;
    }

    public bool Success => Root is not null;
    /// <summary>The AST root; null on failure.</summary>
    public FormulaNode? Root { get; }
    /// <summary>Empty on success.</summary>
    public string ErrorMessage { get; }
    /// <summary>0-based offset in the formula text (after the '=') of the error; -1 on success.</summary>
    public int ErrorPosition { get; }

    internal static ParsedFormula Ok(FormulaNode root) => new(root, "", -1);
    internal static ParsedFormula Fail(string message, int position) => new(null, message, position);
}
