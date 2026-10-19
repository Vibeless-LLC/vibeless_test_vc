namespace Gridline.Core.Parsing;

/// <summary>Kinds of formula token (§5.1).</summary>
public enum TokenKind
{
    Number, String, Error, Boolean, CellRef, Name, Function,
    Colon, Plus, Minus, Star, Slash, Caret, Ampersand, Percent,
    Equal, NotEqual, Less, Greater, LessEqual, GreaterEqual,
    LParen, RParen, Comma, End,
}
