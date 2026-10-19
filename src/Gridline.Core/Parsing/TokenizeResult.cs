namespace Gridline.Core.Parsing;

/// <summary>Outcome of tokenizing (§5.1): the tokens (ending with End), or a message and 0-based position. Never thrown for user input.</summary>
public sealed class TokenizeResult
{
    private TokenizeResult(IReadOnlyList<Token> tokens, string errorMessage, int errorPosition)
    {
        Tokens = tokens;
        ErrorMessage = errorMessage;
        ErrorPosition = errorPosition;
    }

    public bool Success => ErrorMessage.Length == 0;
    /// <summary>The tokens, ending with an End token; empty on failure.</summary>
    public IReadOnlyList<Token> Tokens { get; }
    /// <summary>Empty on success.</summary>
    public string ErrorMessage { get; }
    /// <summary>0-based offset in the formula text of the error; -1 on success.</summary>
    public int ErrorPosition { get; }

    internal static TokenizeResult Ok(List<Token> tokens) => new(tokens, "", -1);
    internal static TokenizeResult Fail(string message, int position) => new([], message, position);
}
