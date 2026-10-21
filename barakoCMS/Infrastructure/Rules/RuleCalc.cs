using System.Globalization;

namespace barakoCMS.Infrastructure.Rules;

/// <summary>An expression in a rule template could not be worked out; the rule fails with this text.</summary>
internal sealed class RuleExpressionException(string message) : Exception(message);

/// <summary>Works out {{= expr }}: numbers, 'text', paths, + - * /, brackets, round, min, max, sum, ifeq.</summary>
internal sealed class RuleCalc(string text, Func<string, List<object?>> read)
{
    private const int MaxDepth = 64;
    private int _at;
    private int _depth;

    public static string Evaluate(string text, Func<string, List<object?>> read)
    {
        var calc = new RuleCalc(text, read);
        try
        {
            var value = calc.Additive();
            calc.Skip();
            if (calc._at < text.Length) throw new RuleExpressionException($"'{text}' has an unexpected '{text[calc._at]}'.");
            return calc.Text(value);
        }
        catch (OverflowException)
        {
            throw new RuleExpressionException($"'{text}' gives a number too large to hold.");
        }
    }

    private object Additive()
    {
        var left = Term();
        while (Peek('+') || Peek('-'))
        {
            var op = text[_at++];
            var right = Num(Term());
            left = op == '+' ? Num(left) + right : Num(left) - right;
        }
        return left;
    }

    private object Term()
    {
        var left = Unary();
        while (Peek('*') || Peek('/'))
        {
            var op = text[_at++];
            var right = Num(Unary());
            if (op == '/' && right == 0) throw new RuleExpressionException($"'{text}' divides by zero.");
            left = op == '*' ? Num(left) * right : Num(left) / right;
        }
        return left;
    }

    private object Unary()
    {
        if (++_depth > MaxDepth) throw new RuleExpressionException($"'{text}' nests too deeply.");
        try
        {
            if (!Peek('-')) return Atom();
            _at++;
            return -Num(Unary());
        }
        finally
        {
            _depth--;
        }
    }

    private object Atom()
    {
        Skip();
        if (_at >= text.Length) throw new RuleExpressionException($"'{text}' ends too early.");
        var ch = text[_at];
        if (ch == '(')
        {
            _at++;
            var inner = Additive();
            Expect(')');
            return inner;
        }
        if (ch == '\'') return Quoted();
        if (char.IsDigit(ch)) return Num(Take(c => char.IsDigit(c) || c == '.'));
        var name = Name();
        if (name.Length == 0) throw new RuleExpressionException($"'{text}' has an unexpected '{ch}'.");
        return Peek('(') ? Call(name) : Text(read(name).FirstOrDefault());
    }

    private object Call(string name)
    {
        _at++;
        if (name.Equals("sum", StringComparison.OrdinalIgnoreCase))
        {
            Skip();
            var path = Name();
            Expect(')');
            return read(path).Sum(v => Num(Text(v)));
        }

        var args = new List<object> { Additive() };
        while (Peek(','))
        {
            _at++;
            args.Add(Additive());
        }
        Expect(')');
        return name.ToLowerInvariant() switch
        {
            "round" when args.Count is 1 or 2 => Math.Round(Num(args[0]), args.Count == 2 ? Digits(args[1]) : 0, MidpointRounding.AwayFromZero),
            "min" => args.Select(Num).Min(),
            "max" => args.Select(Num).Max(),
            "ifeq" when args.Count == 4 => string.Equals(Text(args[0]), Text(args[1]), StringComparison.OrdinalIgnoreCase) ? args[2] : args[3],
            _ => throw new RuleExpressionException($"'{text}' calls an unknown function '{name}' or gives it the wrong number of values."),
        };
    }

    private decimal Num(object value) => value switch
    {
        decimal d => d,
        string { Length: 0 } => 0m,
        string s when RuleConditions.TryNumber(s, out var n) => n,
        _ => throw new RuleExpressionException($"'{text}': '{value}' is not a number."),
    };

    private int Digits(object value) =>
        Num(value) is var n && n is >= 0 and <= 28 && n == decimal.Truncate(n)
            ? (int)n
            : throw new RuleExpressionException($"'{text}' rounds to '{value}' places; use a whole number from 0 to 28.");

    private string Text(object? value) => value switch
    {
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        string s => s,
        _ => RulePath.TextOf(value) ?? "",
    };

    private string Quoted()
    {
        _at++;
        var start = _at;
        while (_at < text.Length && text[_at] != '\'') _at++;
        if (_at >= text.Length) throw new RuleExpressionException($"'{text}' has a quote that is not closed.");
        return text[start.._at++];
    }

    private string Name()
    {
        var start = _at;
        var depth = 0;
        while (_at < text.Length && (depth > 0 || char.IsLetterOrDigit(text[_at]) || text[_at] is '_' or '.' or '$' or '['))
        {
            if (text[_at] == '[') depth++;
            else if (text[_at] == ']') depth--;
            _at++;
        }
        return text[start.._at];
    }

    private string Take(Func<char, bool> keep)
    {
        var start = _at;
        while (_at < text.Length && keep(text[_at])) _at++;
        return text[start.._at];
    }

    private bool Peek(char c)
    {
        Skip();
        return _at < text.Length && text[_at] == c;
    }

    private void Expect(char c)
    {
        if (!Peek(c)) throw new RuleExpressionException($"'{text}' is missing '{c}'.");
        _at++;
    }

    private void Skip()
    {
        while (_at < text.Length && char.IsWhiteSpace(text[_at])) _at++;
    }
}
