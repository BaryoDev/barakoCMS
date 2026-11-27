namespace barakoCMS.Core.Expressions;

internal enum TokenKind
{
    Number,
    Text,
    Name,
    LeftParen,
    RightParen,
    Comma,
    Dot,
    Plus,
    Minus,
    Star,
    Slash,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    End,
}

/// <summary>A token. Keywords such as <c>and</c> and <c>true</c> are Name tokens; the parser tells them apart.</summary>
internal readonly record struct Token(TokenKind Kind, int Position, string Text, decimal Number);
