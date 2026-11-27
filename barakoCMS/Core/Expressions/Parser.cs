using System.Globalization;

namespace barakoCMS.Core.Expressions;

internal sealed record ParsedTree(Node? Root, int Nodes, ExpressionError? Error);

/// <summary>
/// Hand-written recursive descent over the token list. Nesting is counted before each recursive
/// call, so deep input is refused instead of overflowing the stack.
/// </summary>
/// <remarks>
/// Precedence, loosest first: or, and, not, comparison (not chained), + and -, * and /, unary minus.
/// </remarks>
internal sealed class Parser
{
    private static readonly string[] SystemNames = ["id", "status", "createdAt", "updatedAt"];

    private readonly List<Token> _tokens;
    private readonly ExpressionLimits _limits;
    private int _at;
    private int _nest;
    private int _nodes;

    private Parser(List<Token> tokens, ExpressionLimits limits)
    {
        _tokens = tokens;
        _limits = limits;
    }

    private sealed class Stop(ExpressionError error) : Exception
    {
        public ExpressionError Error { get; } = error;
    }

    public static ParsedTree Parse(List<Token> tokens, ExpressionLimits limits)
    {
        var parser = new Parser(tokens, limits);
        try
        {
            var root = parser.ParseExpression();
            if (parser.Peek.Kind != TokenKind.End)
                throw parser.Fail(Rules.Syntax, parser.Peek.Position, "this does not continue the formula");

            return new ParsedTree(root, parser._nodes, null);
        }
        catch (Stop stop)
        {
            return new ParsedTree(null, parser._nodes, stop.Error);
        }
    }

    private Token Peek => _tokens[_at];

    private Token Next() => _tokens[_at < _tokens.Count - 1 ? _at++ : _at];

    private Stop Fail(string rule, int position, string message) => new(new ExpressionError(rule, position, message));

    private bool PeekWord(string word) =>
        Peek.Kind == TokenKind.Name && string.Equals(Peek.Text, word, StringComparison.OrdinalIgnoreCase);

    private Node Make(Node node)
    {
        if (++_nodes > _limits.MaxNodes)
            throw Fail(Rules.NodeLimit, node.Position, $"a formula may have at most {_limits.MaxNodes} parts");

        if (node.Height > _limits.MaxDepth)
            throw Fail(Rules.DepthLimit, node.Position, $"a formula may be at most {_limits.MaxDepth} levels deep");

        return node;
    }

    private void Enter(int position)
    {
        if (++_nest > _limits.MaxDepth)
            throw Fail(Rules.DepthLimit, position, $"a formula may nest at most {_limits.MaxDepth} levels deep");
    }

    private Node ParseExpression()
    {
        Enter(Peek.Position);
        var node = ParseOr();
        _nest--;
        return node;
    }

    private Node ParseOr()
    {
        var left = ParseAnd();
        while (PeekWord("or"))
        {
            var at = Next().Position;
            left = Make(new BinaryNode(at, "or", left, ParseAnd()));
        }

        return left;
    }

    private Node ParseAnd()
    {
        var left = ParseNot();
        while (PeekWord("and"))
        {
            var at = Next().Position;
            left = Make(new BinaryNode(at, "and", left, ParseNot()));
        }

        return left;
    }

    private Node ParseNot()
    {
        if (!PeekWord("not"))
            return ParseComparison();

        var at = Next().Position;
        Enter(at);
        var operand = ParseNot();
        _nest--;
        return Make(new UnaryNode(at, "not", operand));
    }

    private Node ParseComparison()
    {
        var left = ParseSum();
        var op = Peek.Kind switch
        {
            TokenKind.Equal => "=",
            TokenKind.NotEqual => "!=",
            TokenKind.Less => "<",
            TokenKind.LessOrEqual => "<=",
            TokenKind.Greater => ">",
            TokenKind.GreaterOrEqual => ">=",
            _ => null,
        };

        if (op is null)
            return left;

        var at = Next().Position;
        return Make(new BinaryNode(at, op, left, ParseSum()));
    }

    private Node ParseSum()
    {
        var left = ParseProduct();
        while (Peek.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var token = Next();
            left = Make(new BinaryNode(token.Position, token.Kind == TokenKind.Plus ? "+" : "-", left, ParseProduct()));
        }

        return left;
    }

    private Node ParseProduct()
    {
        var left = ParseUnary();
        while (Peek.Kind is TokenKind.Star or TokenKind.Slash)
        {
            var token = Next();
            left = Make(new BinaryNode(token.Position, token.Kind == TokenKind.Star ? "*" : "/", left, ParseUnary()));
        }

        return left;
    }

    private Node ParseUnary()
    {
        if (Peek.Kind != TokenKind.Minus)
            return ParsePrimary();

        var at = Next().Position;
        Enter(at);
        var operand = ParseUnary();
        _nest--;
        return Make(new UnaryNode(at, "-", operand));
    }

    private Node ParsePrimary()
    {
        var token = Next();
        switch (token.Kind)
        {
            case TokenKind.Number:
                return Make(new Literal(token.Position, token.Number, ExprType.Number));
            case TokenKind.Text:
                return Make(new Literal(token.Position, token.Text, ExprType.Text));
            case TokenKind.LeftParen:
                var inner = ParseExpression();
                Expect(TokenKind.RightParen, "expected a closing bracket");
                return inner;
            case TokenKind.Name:
                return ParseWord(token);
            default:
                throw Fail(Rules.Syntax, token.Position, token.Kind == TokenKind.End ? "the formula ends too soon" : "a value was expected here");
        }
    }

    private void Expect(TokenKind kind, string message)
    {
        if (Peek.Kind != kind)
            throw Fail(Rules.Syntax, Peek.Position, message);

        Next();
    }

    private Node ParseWord(Token token)
    {
        var word = token.Text.ToLowerInvariant();
        if (word is "and" or "or" or "not")
            throw Fail(Rules.Syntax, token.Position, "a value was expected here");

        if (word is "true" or "false")
            return Make(new Literal(token.Position, word == "true", ExprType.Bool));

        if (word == "null")
            return Make(new Literal(token.Position, null, ExprType.Null));

        if (Peek.Kind == TokenKind.LeftParen)
            return word == "date" ? ParseDate(token) : ParseCall(token);

        if (Peek.Kind != TokenKind.Dot)
            return Make(NameOf(token));

        if (word != "data")
            throw Fail(Rules.MemberAccess, Peek.Position, "a name has no members; only data.Field is allowed");

        Next();
        if (Peek.Kind != TokenKind.Name)
            throw Fail(Rules.Syntax, Peek.Position, "a field name was expected after data.");

        var field = Next();
        return Make(new NameNode(token.Position, field.Text, false));
    }

    private static NameNode NameOf(Token token)
    {
        var system = SystemNames.FirstOrDefault(n => string.Equals(n, token.Text, StringComparison.OrdinalIgnoreCase));
        return system is null ? new NameNode(token.Position, token.Text, false) : new NameNode(token.Position, system, true);
    }

    private Node ParseDate(Token token)
    {
        Next();
        var text = Peek;
        if (text.Kind != TokenKind.Text)
            throw Fail(Rules.BadDate, text.Position, "date() takes a quoted day such as \"2026-11-22\"");

        Next();
        Expect(TokenKind.RightParen, "expected a closing bracket");
        if (!DateTime.TryParseExact(text.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            throw Fail(Rules.BadDate, text.Position, "date() takes a real day written yyyy-MM-dd");

        return Make(new Literal(token.Position, day, ExprType.Date));
    }

    private Node ParseCall(Token token)
    {
        Next();
        var args = new List<Node>();
        if (Peek.Kind != TokenKind.RightParen)
        {
            args.Add(ParseExpression());
            while (Peek.Kind == TokenKind.Comma)
            {
                Next();
                args.Add(ParseExpression());
            }
        }

        Expect(TokenKind.RightParen, "expected a comma or a closing bracket");
        return Make(new CallNode(token.Position, token.Text, args));
    }
}
