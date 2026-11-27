using System.Globalization;
using System.Text;

namespace barakoCMS.Core.Expressions;

/// <summary>Turns the source into tokens, one pass, no backtracking. Stops at the first bad character.</summary>
internal static class Lexer
{
    public static ExpressionError? TryLex(string source, List<Token> tokens)
    {
        var at = 0;
        while (at < source.Length)
        {
            var c = source[at];
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                at++;
                continue;
            }

            var error = IsDigit(c) ? ReadNumber(source, ref at, tokens)
                : c == '"' ? ReadText(source, ref at, tokens)
                : IsNameStart(c) ? ReadName(source, ref at, tokens)
                : ReadSymbol(source, ref at, tokens);
            if (error is not null)
                return error;
        }

        tokens.Add(new Token(TokenKind.End, source.Length, string.Empty, 0));
        return null;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_';

    private static ExpressionError? ReadNumber(string source, ref int at, List<Token> tokens)
    {
        var start = at;
        while (at < source.Length && IsDigit(source[at]))
            at++;

        if (at < source.Length && source[at] == '.')
        {
            if (at + 1 >= source.Length || !IsDigit(source[at + 1]))
                return new ExpressionError(Rules.BadNumber, at, "a number needs digits after the decimal point");

            at++;
            while (at < source.Length && IsDigit(source[at]))
                at++;
        }

        if (at < source.Length && source[at] is 'e' or 'E')
            return new ExpressionError(Rules.Exponent, at, "numbers have no exponent form, write the digits out");

        if (at < source.Length && IsNameStart(source[at]))
            return new ExpressionError(Rules.BadNumber, at, "a number runs straight into a name");

        if (!decimal.TryParse(source.AsSpan(start, at - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            return new ExpressionError(Rules.BadNumber, start, "the number is too large");

        tokens.Add(new Token(TokenKind.Number, start, string.Empty, number));
        return null;
    }

    private static ExpressionError? ReadText(string source, ref int at, List<Token> tokens)
    {
        var start = at++;
        var text = new StringBuilder();
        while (at < source.Length)
        {
            var c = source[at];
            if (c == '"')
            {
                at++;
                tokens.Add(new Token(TokenKind.Text, start, text.ToString(), 0));
                return null;
            }

            if (c == '\\')
            {
                if (at + 1 >= source.Length || source[at + 1] is not ('"' or '\\'))
                    return new ExpressionError(Rules.BadString, at, "a backslash may only be followed by a quote or a backslash");

                text.Append(source[at + 1]);
                at += 2;
                continue;
            }

            text.Append(c);
            at++;
        }

        return new ExpressionError(Rules.BadString, start, "the text has no closing quote");
    }

    private static ExpressionError? ReadName(string source, ref int at, List<Token> tokens)
    {
        var start = at;
        while (at < source.Length && (IsNameStart(source[at]) || char.IsLetterOrDigit(source[at])))
            at++;

        tokens.Add(new Token(TokenKind.Name, start, source[start..at], 0));
        return null;
    }

    private static ExpressionError? ReadSymbol(string source, ref int at, List<Token> tokens)
    {
        var start = at;
        var c = source[at++];
        var next = at < source.Length ? source[at] : '\0';

        TokenKind? kind = c switch
        {
            '(' => TokenKind.LeftParen,
            ')' => TokenKind.RightParen,
            ',' => TokenKind.Comma,
            '.' => TokenKind.Dot,
            '+' => TokenKind.Plus,
            '-' => TokenKind.Minus,
            '*' => TokenKind.Star,
            '/' => TokenKind.Slash,
            '=' => TokenKind.Equal,
            '!' when next == '=' => TokenKind.NotEqual,
            '<' when next == '=' => TokenKind.LessOrEqual,
            '<' => TokenKind.Less,
            '>' when next == '=' => TokenKind.GreaterOrEqual,
            '>' => TokenKind.Greater,
            _ => null,
        };

        if (kind is null)
            return new ExpressionError(Rules.UnexpectedCharacter, start, "this character is not part of the language");

        if (kind is TokenKind.NotEqual or TokenKind.LessOrEqual or TokenKind.GreaterOrEqual)
            at++;

        tokens.Add(new Token(kind.Value, start, string.Empty, 0));
        return null;
    }
}
