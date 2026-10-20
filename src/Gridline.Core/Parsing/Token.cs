using Gridline.Core.Addressing;

namespace Gridline.Core.Parsing;

/// <summary>
/// One formula token (§5.1). <see cref="Lexeme"/> is the exact source text (kept for §5.5 number printing).
/// Payload by kind: Number -> <see cref="Number"/>; String -> <see cref="Text"/> (unescaped); Error -> <see cref="Text"/>
/// (the code exactly as written, R52); Boolean -> <see cref="Boolean"/>; CellRef -> <see cref="Reference"/>; Name -> <see cref="Text"/> (as written);
/// Function -> <see cref="Text"/> (upper-cased). Payloads of other kinds are default.
/// </summary>
public readonly record struct Token(
    TokenKind Kind,
    int Position,
    string Lexeme,
    double Number = 0,
    string Text = "",
    bool Boolean = false,
    CellReference Reference = default);
