using System.Globalization;
using System.Text;
using System.Text.Json;
using barakoCMS.Core.Expressions;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>The rows one dataset read for a template, as the run's user may see them.</summary>
/// <param name="Slug">The saved query the rows came from.</param>
/// <param name="Rows">The rows, each holding only the fields the query selects.</param>
/// <param name="Failure">Why the dataset has no rows to render, or null. A template that reads it fails with this message.</param>
internal sealed record TemplateDataset(string Slug, IReadOnlyList<Dictionary<string, object>> Rows, string? Failure = null);

/// <summary>The rules a template's declared datasets follow.</summary>
internal static class TemplateDatasets
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "data", "row", "id", "status", "createdAt", "updatedAt", "createdBy", "contentType", "transition", "links", "actions", "site",
    };

    /// <summary>A name the formula and the loop readers can tell from every other name a template uses.</summary>
    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= 40
        && char.IsAsciiLetter(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
        && !name.Contains("__", StringComparison.Ordinal)
        && !Reserved.Contains(name);

    /// <summary>
    /// The query as it is run for a template: one row past the cap when its limit reaches the cap, so
    /// a dataset that holds more than a template renders is told apart from one that holds exactly that.
    /// </summary>
    /// <remarks>A copy, so the stored definition is never changed.</remarks>
    public static QueryDefinition Probe(QueryDefinition definition)
    {
        if (definition.Limit < TemplateExpression.MaxDatasetRows) return definition;

        var copy = System.Text.Json.JsonSerializer.Deserialize<QueryDefinition>(System.Text.Json.JsonSerializer.Serialize(definition))!;
        copy.Limit = TemplateExpression.MaxDatasetRows + 1;
        return copy;
    }
}

/// <summary>A template that cannot be rendered at all: a dataset or an output past its cap, or a dataset that did not run.</summary>
/// <remarks>The message names the dataset and the limit and never a value.</remarks>
internal sealed class TemplateRenderException(string message) : Exception(message);

/// <summary>
/// Evaluates the expression inside <c>{{= ... }}</c> with the shared evaluator.
/// </summary>
/// <remarks>
/// The evaluator reads one-level names only (<c>data.Field</c>), so the names a template adds are
/// read here, before it runs: a path such as <c>data.Customer.Name</c> or <c>row.Product.Name</c>, and
/// the dataset functions <c>sum</c>, <c>count</c>, <c>avg</c>, <c>min</c>, <c>max</c>, <c>first</c>,
/// <c>last</c> and <c>lookup</c>. Each is replaced by a name of its own that holds the value, so a
/// value is only ever a value to the evaluator, never part of the formula text. A name an author
/// writes that starts with two underscores is refused, so it cannot reach one of these.
/// </remarks>
internal static class TemplateFormulas
{
    private const string Synthetic = "__v";

    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "true", "false", "null",
    };

    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "status", "createdAt", "updatedAt",
    };

    private static readonly HashSet<string> Aggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        "sum", "count", "avg", "min", "max", "first", "last",
    };

    /// <summary>The evaluated value, or false with a note on the context saying why not. The note never holds a value.</summary>
    public static bool TryEvaluate(string source, Content content, TemplateContext context, out object? value)
    {
        value = null;
        var binder = new Binder(content, context, dry: false);
        var rewritten = binder.Rewrite(source);
        if (rewritten is null)
        {
            context.Note(binder.Error ?? "An expression could not be read.");
            return false;
        }

        var parsed = ExpressionEngine.ParseAndCheck(rewritten, binder.TypeOf, refuseClock: false);
        if (parsed.Expression is null)
        {
            context.Note($"An expression could not be evaluated: {Describe(parsed.Errors.FirstOrDefault())}.");
            return false;
        }

        var clock = new ZonedClock(context.TimeZone ?? TimeZoneInfo.Utc);
        var result = ExpressionEngine.Evaluate(parsed.Expression, binder.Read, clock);
        if (result.Note is not null)
        {
            context.Note($"An expression gave no value: {Describe(result.Note)}.");
            return false;
        }

        value = result.Value is DateTime moment ? Wrap(moment, context) : result.Value;
        return true;
    }

    /// <summary>Why <paramref name="source"/> could not be read, judged without an entry, or null when it can.</summary>
    public static string? Problem(string source)
    {
        var binder = new Binder(new Content(), TemplateContext.Unprepared, dry: true);
        var rewritten = binder.Rewrite(source);
        if (rewritten is null) return binder.Error;

        var read = ExpressionEngine.Read(rewritten);
        return read.Error is null ? null : Describe(read.Error);
    }

    private static string Describe(ExpressionError? error) =>
        error is null ? "it is not valid" : $"{error.Message} (rule {error.Rule}, position {error.Position})";

    private static TemplateExpression.Moment Wrap(DateTime moment, TemplateContext context) =>
        moment.Kind == DateTimeKind.Unspecified
            ? new TemplateExpression.Moment(TimeZoneInfo.ConvertTimeToUtc(moment, context.TimeZone ?? TimeZoneInfo.Utc))
            : new TemplateExpression.Moment(moment.ToUniversalTime());

    /// <summary>The clock a template reads: the same instant, in the tenant's zone.</summary>
    private sealed class ZonedClock(TimeZoneInfo zone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    /// <summary>One read of one expression: the names it binds, and how it reads them.</summary>
    private sealed class Binder(Content content, TemplateContext context, bool dry)
    {
        private readonly List<(object? Value, ExprType Type)> _bound = [];
        private bool _insideLookup;

        public string? Error { get; private set; }

        public ExprType? TypeOf(string field)
        {
            if (field.StartsWith(Synthetic, StringComparison.Ordinal))
            {
                return int.TryParse(field.AsSpan(Synthetic.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var at)
                    && at < _bound.Count ? _bound[at].Type : null;
            }

            return Find(field) is { } raw ? TypeFrom(Normalize(raw)) : null;
        }

        public object? Read(NameRef name)
        {
            if (name.IsSystem)
            {
                return name.Name.ToLowerInvariant() switch
                {
                    "id" => content.Id.ToString(),
                    "status" => content.Status.ToString(),
                    "createdat" => content.CreatedAt,
                    _ => content.UpdatedAt,
                };
            }

            if (name.Name.StartsWith(Synthetic, StringComparison.Ordinal))
            {
                return int.TryParse(name.Name.AsSpan(Synthetic.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var at)
                    && at < _bound.Count ? _bound[at].Value : null;
            }

            return Normalize(Find(name.Name));
        }

        private object? Find(string field)
        {
            if (content.Data is null) return null;

            foreach (var (key, value) in content.Data)
            {
                if (string.Equals(key, field, StringComparison.OrdinalIgnoreCase)) return value;
            }

            return null;
        }

        /// <summary>The formula with each added name replaced by a bound one, or null with <see cref="Error"/> set.</summary>
        public string? Rewrite(string source)
        {
            var tokens = new List<Token>();
            if (Lexer.TryLex(source, tokens) is { } lexError)
            {
                Error = $"An expression could not be read: {Describe(lexError)}.";
                return null;
            }

            var output = new StringBuilder(source.Length + 16);
            var at = 0;
            while (tokens[at].Kind != TokenKind.End)
            {
                var token = tokens[at];
                if (token.Kind != TokenKind.Name || Words.Contains(token.Text))
                {
                    output.Append(source, token.Position, tokens[at + 1].Position - token.Position);
                    at++;
                    continue;
                }

                var next = Name(source, tokens, at, out var replacement);
                if (next < 0) return null;

                if (replacement is null) output.Append(source, token.Position, tokens[next].Position - token.Position);
                else output.Append(replacement).Append(' ');
                at = next;
            }

            return output.ToString();
        }

        /// <summary>Reads the name or call at <paramref name="at"/>. Returns where reading goes on, or -1 with an error.</summary>
        private int Name(string source, List<Token> tokens, int at, out string? replacement)
        {
            replacement = null;
            var name = tokens[at].Text;

            if (name.StartsWith("__", StringComparison.Ordinal))
            {
                Error = "An expression names something that starts with two underscores, and that is not allowed.";
                return -1;
            }

            if (tokens[at + 1].Kind == TokenKind.LeftParen)
            {
                return Call(source, tokens, at, out replacement);
            }

            var end = at + 1;
            var path = new List<string> { name };
            while (tokens[end].Kind == TokenKind.Dot && tokens[end + 1].Kind == TokenKind.Name)
            {
                if (!Touching(tokens, end - 1, tokens[end - 1].Text.Length) || !Touching(tokens, end, 1))
                {
                    Error = "A name in an expression is written without spaces around its dots.";
                    return -1;
                }

                path.Add(tokens[end + 1].Text);
                end += 2;
            }

            return Path(path, end, out replacement);
        }

        /// <summary>Whether the token at <paramref name="at"/>, <paramref name="width"/> characters long in the source, runs straight into the next one. A dot token carries no text, so its width is given.</summary>
        private static bool Touching(List<Token> tokens, int at, int width) =>
            tokens[at].Position + width == tokens[at + 1].Position;

        private int Path(List<string> path, int end, out string? replacement)
        {
            replacement = null;
            var head = path[0];

            if (path.Count == 1 && !SystemNames.Contains(head) && !head.Equals("data", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Datasets?.ContainsKey(head) == true)
                {
                    Error = $"The dataset '{head}' is read through sum, count, avg, min, max, first, last or lookup.";
                    return -1;
                }

                return end;
            }

            if (path.Count == 1) return end;

            if (head.Equals("data", StringComparison.OrdinalIgnoreCase) && path.Count == 2) return end;

            if (context.Datasets?.ContainsKey(head) == true)
            {
                Error = $"The dataset '{head}' is read through sum, count, avg, min, max, first, last or lookup.";
                return -1;
            }

            var key = string.Join('.', path);
            object? raw = null;
            var known = dry || TemplateExpression.TryValue(key, content, context, out raw);
            if (!known)
            {
                Error = $"An expression reads '{key}', which is not a name this template can read.";
                return -1;
            }

            replacement = Bind(raw, head.Equals("row", StringComparison.OrdinalIgnoreCase) ? ExprType.Number : ExprType.Text);
            return end;
        }

        private string Bind(object? raw, ExprType whenEmpty)
        {
            var value = Normalize(raw);
            var type = value is null ? whenEmpty : TypeFrom(value) ?? whenEmpty;
            _bound.Add((value, type));
            return "data." + Synthetic + (_bound.Count - 1).ToString(CultureInfo.InvariantCulture);
        }

        private int Call(string source, List<Token> tokens, int at, out string? replacement)
        {
            replacement = null;
            var function = tokens[at].Text;
            var close = Closing(tokens, at + 1);
            if (close < 0)
            {
                Error = "An expression has a parenthesis that is never closed.";
                return -1;
            }

            if (function.Equals("lookup", StringComparison.OrdinalIgnoreCase))
            {
                return Lookup(source, tokens, at, close, out replacement);
            }

            if (Aggregates.Contains(function) && ColumnOf(tokens, at + 2, close) is { } column)
            {
                return Aggregate(function.ToLowerInvariant(), column, close + 1, out replacement);
            }

            return at + 1;
        }

        /// <summary>The dataset and column a call's one argument names, or null when it is anything else.</summary>
        private (string Dataset, string? Column)? ColumnOf(List<Token> tokens, int from, int close)
        {
            if (tokens[from].Kind != TokenKind.Name) return null;

            var segments = new List<string> { tokens[from].Text };
            var at = from + 1;
            while (at < close && tokens[at].Kind == TokenKind.Dot && tokens[at + 1].Kind == TokenKind.Name)
            {
                segments.Add(tokens[at + 1].Text);
                at += 2;
            }

            if (at != close) return null;
            if (!dry && context.Datasets?.ContainsKey(segments[0]) != true) return null;
            if (dry && segments[0].Equals("data", StringComparison.OrdinalIgnoreCase)) return null;

            return (segments[0], segments.Count == 1 ? null : string.Join('.', segments.Skip(1)));
        }

        private static int Closing(List<Token> tokens, int open)
        {
            var depth = 0;
            for (var i = open; i < tokens.Count; i++)
            {
                if (tokens[i].Kind == TokenKind.LeftParen) depth++;
                else if (tokens[i].Kind == TokenKind.RightParen && --depth == 0) return i;
            }

            return -1;
        }

        private int Aggregate(string function, (string Dataset, string? Column) read, int next, out string? replacement)
        {
            replacement = null;
            if (dry)
            {
                replacement = Bind(null, ExprType.Number);
                return next;
            }

            var rows = context.Datasets![read.Dataset].Rows;
            var values = new List<object?>(rows.Count);
            foreach (var row in rows)
            {
                if (read.Column is null) values.Add(true);
                else values.Add(Cell(row, read.Column));
            }

            if (read.Column is null && function != "count")
            {
                Error = $"'{function}' reads a column of a dataset, as in {function}({read.Dataset}.Column).";
                return -1;
            }

            var result = Fold(function, values, read.Column ?? read.Dataset);
            if (result.Failed)
            {
                Error = result.Problem;
                return -1;
            }

            replacement = Bind(result.Value, function is "first" or "last" ? ExprType.Text : ExprType.Number);
            return next;
        }

        private readonly record struct Folded(object? Value, string? Problem)
        {
            public bool Failed => Problem is not null;
        }

        private static Folded Fold(string function, List<object?> values, string label)
        {
            var present = values.Where(v => v is not null and not "").ToList();
            switch (function)
            {
                case "count": return new Folded((decimal)present.Count, null);
                case "first": return new Folded(present.FirstOrDefault(), null);
                case "last": return new Folded(present.LastOrDefault(), null);
            }

            var numbers = new List<decimal>(present.Count);
            foreach (var value in present)
            {
                if (!TemplateExpression.TryNumber(value, out var number))
                {
                    return new Folded(null, $"'{function}' met a value in '{label}' that is not a number.");
                }

                numbers.Add(number);
            }

            return function switch
            {
                "sum" => new Folded(numbers.Sum(), null),
                "avg" => new Folded(numbers.Count == 0 ? null : numbers.Sum() / numbers.Count, null),
                "min" => new Folded(numbers.Count == 0 ? null : numbers.Min(), null),
                _ => new Folded(numbers.Count == 0 ? null : numbers.Max(), null),
            };
        }

        private static object? Cell(Dictionary<string, object> row, string column)
        {
            foreach (var (key, value) in row)
            {
                if (string.Equals(key, column, StringComparison.OrdinalIgnoreCase)) return value;
            }

            return null;
        }

        private int Lookup(string source, List<Token> tokens, int at, int close, out string? replacement)
        {
            replacement = null;
            if (_insideLookup)
            {
                Error = "A lookup cannot hold another lookup.";
                return -1;
            }

            var parts = Split(tokens, at + 2, close);
            if (parts.Count != 4 || !IsWord(tokens, parts[0]) || !(IsWord(tokens, parts[1]) || IsText(tokens, parts[1]))
                || !(IsWord(tokens, parts[3]) || IsText(tokens, parts[3])))
            {
                Error = "lookup takes a dataset, the column to match, the value to find and the column to read.";
                return -1;
            }

            if (Enumerable.Range(parts[2].From, parts[2].To - parts[2].From).Any(i =>
                    tokens[i].Kind == TokenKind.Name && tokens[i].Text.Equals("lookup", StringComparison.OrdinalIgnoreCase)))
            {
                Error = "A lookup cannot hold another lookup.";
                return -1;
            }

            var dataset = tokens[parts[0].From].Text;
            if (dry)
            {
                replacement = Bind(null, ExprType.Text);
                return close + 1;
            }

            if (context.Datasets?.TryGetValue(dataset, out var rows) != true)
            {
                Error = $"The dataset '{dataset}' is not declared on this template.";
                return -1;
            }

            var key = KeyOf(source, tokens, parts[2]);
            if (key.Failed) return -1;

            replacement = Bind(Find(rows!.Rows, Label(tokens, parts[1]), key.Value, Label(tokens, parts[3])), ExprType.Text);
            return close + 1;
        }

        private (bool Failed, object? Value) KeyOf(string source, List<Token> tokens, (int From, int To) range)
        {
            var slice = source[tokens[range.From].Position..tokens[range.To].Position];
            var inner = new Binder(content, context, dry: false) { _insideLookup = true };
            var rewritten = inner.Rewrite(slice);
            var parsed = rewritten is null ? null : ExpressionEngine.ParseAndCheck(rewritten, inner.TypeOf, refuseClock: false);
            if (parsed?.Expression is null)
            {
                Error = inner.Error ?? "The value a lookup finds could not be read.";
                return (true, null);
            }

            var evaluated = ExpressionEngine.Evaluate(parsed.Expression, inner.Read, new ZonedClock(context.TimeZone ?? TimeZoneInfo.Utc));
            if (evaluated.Note is not null)
            {
                Error = "The value a lookup finds could not be worked out.";
                return (true, null);
            }

            return (false, evaluated.Value);
        }

        private static string Label(List<Token> tokens, (int From, int To) range) =>
            string.Concat(Enumerable.Range(range.From, range.To - range.From).Select(i => tokens[i].Text));

        private static object? Find(IReadOnlyList<Dictionary<string, object>> rows, string keyColumn, object? key, string resultColumn)
        {
            foreach (var row in rows)
            {
                var candidate = Cell(row, keyColumn);
                if (Same(candidate, key)) return Cell(row, resultColumn);
            }

            return null;
        }

        private static bool Same(object? candidate, object? key)
        {
            if (candidate is null || key is null) return false;

            if (key is decimal wanted) return TemplateExpression.TryNumber(candidate, out var number) && number == wanted;

            return string.Equals(Convert.ToString(Normalize(candidate), CultureInfo.InvariantCulture), Convert.ToString(key, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        private static List<(int From, int To)> Split(List<Token> tokens, int from, int close)
        {
            var parts = new List<(int From, int To)>();
            var depth = 0;
            var start = from;
            for (var i = from; i < close; i++)
            {
                if (tokens[i].Kind == TokenKind.LeftParen) depth++;
                else if (tokens[i].Kind == TokenKind.RightParen) depth--;
                else if (tokens[i].Kind == TokenKind.Comma && depth == 0)
                {
                    parts.Add((start, i));
                    start = i + 1;
                }
            }

            parts.Add((start, close));
            return parts;
        }

        private static bool IsWord(List<Token> tokens, (int From, int To) part) =>
            part.To > part.From && tokens[part.From].Kind == TokenKind.Name
            && Enumerable.Range(part.From, part.To - part.From).All(i => tokens[i].Kind is TokenKind.Name or TokenKind.Dot);

        private static bool IsText(List<Token> tokens, (int From, int To) part) =>
            part.To - part.From == 1 && tokens[part.From].Kind == TokenKind.Text;
    }

    private static ExprType? TypeFrom(object? value) =>
        Values.TypeOf(value) is var type && type != ExprType.Null ? type : null;

    /// <summary>A stored value as one of the evaluator's five kinds. Text stays text, whatever it looks like.</summary>
    private static object? Normalize(object? raw)
    {
        var unwrapped = raw switch
        {
            JsonElement element => FromJson(element),
            TemplateExpression.Moment moment => moment.Value,
            _ => raw,
        };
        return Values.TryNormalize(unwrapped, out var value) ? value : null;
    }

    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetDecimal(out var number) ? number : null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
