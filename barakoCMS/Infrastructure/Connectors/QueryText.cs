using System.Globalization;
using System.Text;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Connectors;

/// <summary>The outcome of reading a saved query written as text: the definition, or one sentence saying why not.</summary>
internal sealed record QueryTextResult(QueryDefinition? Definition, string? Error)
{
    public bool Ok => Definition is not null;
}

/// <summary>
/// The text form of a saved query: it parses into the typed <see cref="QueryDefinition"/> and prints
/// back from one.
/// </summary>
/// <remarks>
/// Nothing here builds SQL or LINQ. The text is read by a hand lexer into the same typed pieces the
/// JSON form already holds: a content type, filters, a range, a sort, a projection, a limit and the
/// aggregate clauses. A value is a literal or a parameter, and a parameter stays a name until a run
/// binds it. Every name is checked as <c>[A-Za-z0-9_]</c>, with at most one dot where a reference is
/// followed, before the definition is returned. A word that is also a clause keyword is written in
/// double quotes when it is a name.
///
/// The lexer is one pass with no backtracking and no regular expression over the text. The text is at
/// most <see cref="MaxLength"/> characters and <see cref="MaxTerms"/> terms. A message names the
/// position and the rule and never repeats more than a short, printable piece of what was written.
/// </remarks>
internal static class QueryText
{
    public const int MaxLength = 2000;
    public const int MaxTerms = 50;

    /// <summary>The same cap as the filters of a saved query have always had.</summary>
    public const int MaxFilters = 10;

    /// <summary>The refusal for a query, in either form, that lists more than <see cref="MaxTerms"/> fields, filters or measures.</summary>
    public static string? TermRefusal(int fields, int filters, int measures) =>
        fields > MaxTerms || filters > MaxTerms || measures > MaxTerms
            ? $"A query may list at most {MaxTerms} fields, filters or measures."
            : null;

    private const int MaxNamePart = 64;
    private const int MaxEcho = 24;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "from", "where", "and", "or", "not", "in", "order", "by", "select", "limit", "desc", "asc",
        "group", "as", "to", "sum", "count", "avg", "min", "max", "like", "having", "join", "union",
        "insert", "update", "delete", "drop", "null", "is",
    };

    private static readonly string[] Functions = ["sum", "count", "avg", "min", "max"];
    private static readonly string[] Buckets = ["day", "week", "month", "year"];
    private static readonly string[] Relatives = ["today", "last7Days", "last30Days", "thisMonth", "lastMonth", "thisYear"];

    /// <summary>The names every entry has, matched in exactly this spelling.</summary>
    public static readonly string[] SystemFields = ["id", "status", "createdAt", "updatedAt"];

    private static readonly Dictionary<string, string> OpWords = new(StringComparer.Ordinal)
    {
        ["="] = "eq", ["!="] = "ne", ["<"] = "lt", ["<="] = "lte", [">"] = "gt", [">="] = "gte",
    };

    private static readonly Dictionary<string, string> OpSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eq"] = "=", ["ne"] = "!=", ["lt"] = "<", ["lte"] = "<=", ["gt"] = ">", ["gte"] = ">=", ["contains"] = "contains",
    };

    // ---- printing ---------------------------------------------------------------------------

    /// <summary>The text form of a definition, in one fixed order: the form a parse of it gives back.</summary>
    public static string Print(QueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var text = new StringBuilder("from ").Append(Name(definition.ContentType));

        var filters = definition.Filters ?? [];
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            text.Append(i == 0 ? " where " : " and ")
                .Append(Name(filter.Field)).Append(' ')
                .Append(OpSymbols.TryGetValue(filter.Op ?? "", out var symbol) ? symbol : filter.Op)
                .Append(' ')
                .Append(filter.Param is { Length: > 0 } param ? "@" + param : Literal(filter.Value));
        }

        if (definition.Range is { } range && !string.IsNullOrEmpty(range.Field))
        {
            text.Append(" in ").Append(Name(range.Field));
            if (!string.IsNullOrEmpty(range.Relative))
            {
                text.Append(' ').Append(range.Relative);
            }
            else
            {
                if (!string.IsNullOrEmpty(range.From)) text.Append(' ').Append(range.From);
                text.Append(" to");
                if (!string.IsNullOrEmpty(range.To)) text.Append(' ').Append(range.To);
            }
        }

        if (definition.GroupBy is { Count: > 0 } groups)
        {
            text.Append(" group by ").AppendJoin(", ", groups.Select(g =>
                string.IsNullOrEmpty(g.Bucket) ? Name(g.Field) : Name(g.Field) + " " + g.Bucket));
        }

        if (definition.Measures is { Count: > 0 } measures)
        {
            text.Append(' ').AppendJoin(", ", measures.Select(m =>
                m.Fn + (string.IsNullOrEmpty(m.Field) ? "" : " " + (string.IsNullOrEmpty(m.Via) ? Name(m.Field) : Name(m.Via + "." + m.Field)))
                + " as " + m.As));
        }

        if (!string.IsNullOrWhiteSpace(definition.SortField))
        {
            text.Append(" order by ").Append(Name(definition.SortField));
            if (definition.Descending) text.Append(" desc");
        }

        if (definition.Fields is { Count: > 0 } fields)
        {
            text.Append(" select ").AppendJoin(", ", fields.Select(Name));
        }

        if (!definition.IsAggregate)
        {
            text.Append(" limit ").Append(definition.Limit.ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>
    /// A name, in quotes when it is a keyword. A dotted reference name is quoted whole when any part
    /// needs it, because a quoted part followed by <c>.Name</c> is two tokens and reads back as neither.
    /// </summary>
    private static string Name(string name)
    {
        if (string.IsNullOrEmpty(name)) return "\"\"";
        return name.Split('.').Any(p => Reserved.Contains(p) || !IsBare(p)) ? Quote(name) : name;
    }

    private static string Literal(string? value)
    {
        value ??= "";
        return value.Length > 0 && IsBare(value) && !Reserved.Contains(value) && value.Any(char.IsAsciiLetterOrDigit)
            ? value
            : Quote(value);
    }

    private static string Quote(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            if (c is '"' or '\\') quoted.Append('\\');
            quoted.Append(c);
        }

        return quoted.Append('"').ToString();
    }

    private static bool IsBare(string value) => value.Length > 0 && value.All(IsWordChar);

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or ':' or '+';

    // ---- parameters -------------------------------------------------------------------------

    /// <summary>
    /// Whether a parameter name, without its <c>@</c>, is one a run can bind: <c>id</c>,
    /// <c>createdAt</c>, <c>data.Field</c> or <c>actions.N.name</c>.
    /// </summary>
    public static bool IsParameter(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name is "id" or "createdAt") return true;

        var parts = name.Split('.');
        if (parts is ["data", var field]) return IsNamePart(field);
        if (parts is ["actions", var step, var output])
            return step.Length is >= 1 and <= 4 && step.All(char.IsAsciiDigit) && IsNamePart(output);

        return false;
    }

    internal static bool IsNamePart(string part) =>
        part.Length is >= 1 and <= MaxNamePart && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>A field name as a query may hold it: parts of <c>[A-Za-z0-9_]</c>, at most one dot when a reference is allowed.</summary>
    internal static bool IsFieldName(string? name, bool allowDot)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var parts = name.Split('.');
        return parts.Length <= (allowDot ? 2 : 1) && parts.All(IsNamePart);
    }

    // ---- parsing ----------------------------------------------------------------------------

    private enum Kind { Word, Text, Param, Op, Comma }

    private readonly record struct Tok(Kind Kind, string Text, int At, bool Quoted = false);

    /// <summary>Reads text into a definition without a name or slug. Never throws on user text.</summary>
    public static QueryTextResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Fail("The query text is empty.");
        if (text.Length > MaxLength) return Fail($"The query text is {text.Length} characters. At most {MaxLength} are allowed.");

        var tokens = new List<Tok>();
        var lexError = Lex(text, tokens);
        if (lexError is not null) return Fail(lexError);

        if (tokens.Count > MaxTerms) return Fail($"The query has {tokens.Count} terms. At most {MaxTerms} are allowed.");

        var reader = new Reader(tokens);
        try
        {
            return new QueryTextResult(reader.Read(), null);
        }
        catch (QueryTextException e)
        {
            return Fail(e.Message);
        }
    }

    private static QueryTextResult Fail(string message) => new(null, message);

    private static string? Lex(string text, List<Tok> tokens)
    {
        var at = 0;
        while (at < text.Length)
        {
            var c = text[at];
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                at++;
                continue;
            }

            if (c is '"' or '\'')
            {
                var error = ReadText(text, ref at, tokens);
                if (error is not null) return error;
            }
            else if (c == '@')
            {
                var start = at++;
                while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] is '_' or '.')) at++;
                tokens.Add(new Tok(Kind.Param, text[(start + 1)..at], start));
            }
            else if (c == ',')
            {
                tokens.Add(new Tok(Kind.Comma, ",", at++));
            }
            else if (c is '=' or '<' or '>' or '!')
            {
                var start = at++;
                if (at < text.Length && text[at] == '=') at++;
                tokens.Add(new Tok(Kind.Op, text[start..at], start));
            }
            else if (IsWordChar(c))
            {
                var start = at;
                while (at < text.Length && IsWordChar(text[at])) at++;
                tokens.Add(new Tok(Kind.Word, text[start..at], start));
            }
            else
            {
                return Unexpected(c, at);
            }

            if (tokens.Count > MaxTerms) return $"The query has more than {MaxTerms} terms. At most {MaxTerms} are allowed.";
        }

        return null;
    }

    private static string? ReadText(string text, ref int at, List<Tok> tokens)
    {
        var start = at;
        var quote = text[at++];
        var value = new StringBuilder();
        while (at < text.Length)
        {
            var c = text[at++];
            if (c == quote)
            {
                tokens.Add(new Tok(Kind.Text, value.ToString(), start, Quoted: true));
                return null;
            }

            if (c == '\\')
            {
                if (at >= text.Length || text[at] is not ('"' or '\'' or '\\'))
                    return $"A backslash in a string must be followed by a quote or another backslash, at position {at}.";
                c = text[at++];
            }
            else if (char.IsControl(c))
            {
                return $"A string holds no control character, at position {at - 1}.";
            }

            value.Append(c);
        }

        return $"The string that starts at position {start} is not closed.";
    }

    private static string Unexpected(char c, int at)
    {
        var lookalike = c > 127 && char.GetUnicodeCategory(c) is UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherPunctuation;
        return lookalike
            ? $"A quote must be a plain \" or ', not a look-alike, at position {at}."
            : c > 127
                ? $"Only plain ASCII is accepted outside a string, at position {at}."
                : $"The character at position {at} is not part of a query.";
    }

    private sealed class QueryTextException(string message) : Exception(message);

    private sealed class Reader(List<Tok> toks)
    {
        private int _i;

        private Tok? Peek => _i < toks.Count ? toks[_i] : null;

        private bool PeekWord(string word) => Peek is { Kind: Kind.Word, Quoted: false } t && string.Equals(t.Text, word, StringComparison.OrdinalIgnoreCase);

        private bool PeekAny(string[] words) => Peek is { Kind: Kind.Word } t && words.Any(w => string.Equals(t.Text, w, StringComparison.OrdinalIgnoreCase));

        private Tok Next(string expected)
        {
            if (Peek is not { } t) throw new QueryTextException($"The query ends where {expected} was expected.");
            _i++;
            return t;
        }

        private static string Echo(Tok t)
        {
            var shown = new string(t.Text.Where(c => c >= ' ' && c != '\u007f').Take(MaxEcho).ToArray());
            return t.Text.Length > MaxEcho ? shown + "..." : shown;
        }

        private static QueryTextException Bad(Tok t, string rule) => new($"{rule} (at position {t.At}, near '{Echo(t)}').");

        public QueryDefinition Read()
        {
            if (!PeekWord("from")) throw new QueryTextException("A query starts with 'from' and a content type.");
            _i++;

            var definition = new QueryDefinition { Fields = [], Limit = QueryDefinition.DefaultLimit };
            definition.ContentType = Field("a content type", allowDot: false);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var limitSeen = false;
            while (Peek is { } t)
            {
                if (t.Kind != Kind.Word || t.Quoted) throw Bad(t, "A clause was expected: where, in, group by, order by, select or limit");

                var word = t.Text.ToLowerInvariant();
                var isMeasure = Functions.Contains(word);
                var clause = isMeasure ? "measures" : word;
                if (clause is not ("where" or "in" or "group" or "measures" or "order" or "select" or "limit"))
                    throw Bad(t, "A clause was expected: where, in, group by, order by, select or limit");
                if (!seen.Add(clause)) throw Bad(t, $"'{(isMeasure ? "measures" : word)}' is written twice");

                if (!isMeasure) _i++;
                switch (clause)
                {
                    case "where": Where(definition); break;
                    case "in": In(definition); break;
                    case "group": Group(definition); break;
                    case "measures": Measures(definition); break;
                    case "order": Order(definition); break;
                    case "select": Select(definition); break;
                    case "limit": limitSeen = true; Limit(definition); break;
                }
            }

            if (definition.IsAggregate && limitSeen) throw new QueryTextException("An aggregate takes no limit.");
            if (definition.IsAggregate && definition.Fields.Count > 0) throw new QueryTextException("An aggregate returns its figures and takes no select.");
            if (definition.GroupBy is { Count: > 0 } && !definition.IsAggregate) throw new QueryTextException("'group by' needs at least one measure such as 'count as Total'.");

            return definition;
        }

        private string Field(string what, bool allowDot)
        {
            var t = Next(what);
            if (t.Kind != Kind.Word && !(t.Kind == Kind.Text && t.Quoted)) throw Bad(t, $"Expected {what}");
            if (t.Kind == Kind.Word && Reserved.Contains(t.Text)) throw Bad(t, $"'{Echo(t)}' is a reserved word. Write a name that is one in double quotes");
            if (!IsFieldName(t.Text, allowDot))
            {
                throw Bad(t, allowDot
                    ? $"A name is letters, digits and underscores, with at most one dot to follow a reference"
                    : $"A name is letters, digits and underscores, with no dot or space");
            }

            return t.Text;
        }

        private void Where(QueryDefinition definition)
        {
            definition.Filters = [];
            while (true)
            {
                if (definition.Filters.Count >= MaxFilters) throw new QueryTextException($"Too many filters. At most {MaxFilters} are allowed.");

                var field = Field("a field", allowDot: false);
                var op = Next("an operator");
                string opName;
                if (op.Kind == Kind.Op && OpWords.TryGetValue(op.Text, out var mapped)) opName = mapped;
                else if (op.Kind == Kind.Word && !op.Quoted && string.Equals(op.Text, "contains", StringComparison.OrdinalIgnoreCase)) opName = "contains";
                else throw Bad(op, "Expected an operator: = != < <= > >= or contains");

                var value = Next("a value");
                var filter = new QueryFilter { Field = field, Op = opName };
                switch (value.Kind)
                {
                    case Kind.Param:
                        if (!IsParameter(value.Text))
                            throw Bad(value, "A parameter is @id, @createdAt, @data.Field or @actions.N.name");
                        filter.Param = value.Text;
                        break;
                    case Kind.Text:
                        filter.Value = value.Text;
                        break;
                    case Kind.Word:
                        if (Reserved.Contains(value.Text) || !value.Text.Any(char.IsAsciiLetterOrDigit))
                            throw Bad(value, "Expected a value. Put text in quotes");
                        filter.Value = value.Text;
                        break;
                    default:
                        throw Bad(value, "Expected a value");
                }

                definition.Filters.Add(filter);

                if (!PeekWord("and")) return;
                _i++;
            }
        }

        private void In(QueryDefinition definition)
        {
            var field = Field("a date field", allowDot: false);
            var range = new QueryRange { Field = field };

            if (Peek is { Kind: Kind.Word, Quoted: false } w && Relatives.FirstOrDefault(r => string.Equals(r, w.Text, StringComparison.OrdinalIgnoreCase)) is { } relative)
            {
                _i++;
                range.Relative = relative;
                definition.Range = range;
                return;
            }

            var any = false;
            if (Peek is { Kind: Kind.Word } first && !PeekWord("to"))
            {
                range.From = Day(first);
                _i++;
                any = true;
            }

            if (PeekWord("to"))
            {
                _i++;
                any = true;
                if (Peek is { Kind: Kind.Word } last && !Reserved.Contains(last.Text))
                {
                    range.To = Day(last);
                    _i++;
                }
            }

            if (!any) throw new QueryTextException($"A range is one of {string.Join(", ", Relatives)}, or yyyy-MM-dd to yyyy-MM-dd.");
            definition.Range = range;
        }

        private static string Day(Tok t) =>
            DateOnly.TryParseExact(t.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? t.Text
                : throw Bad(t, "A day is written yyyy-MM-dd");

        private void Group(QueryDefinition definition)
        {
            if (!PeekWord("by")) throw new QueryTextException("'group' is followed by 'by'.");
            _i++;

            definition.GroupBy = [];
            do
            {
                var field = Field("a field to group on", allowDot: false);
                string? bucket = null;
                if (Peek is { Kind: Kind.Word, Quoted: false } b && Buckets.FirstOrDefault(x => string.Equals(x, b.Text, StringComparison.OrdinalIgnoreCase)) is { } found)
                {
                    bucket = found;
                    _i++;
                }

                definition.GroupBy.Add(new QueryGroup { Field = field, Bucket = bucket });
            }
            while (Comma());
        }

        private void Measures(QueryDefinition definition)
        {
            definition.Measures = [];
            do
            {
                var fn = Next("a function");
                if (fn.Kind != Kind.Word || !Functions.Contains(fn.Text, StringComparer.OrdinalIgnoreCase))
                    throw Bad(fn, "Expected sum, count, avg, min or max");

                var measure = new QueryMeasure { Fn = fn.Text.ToLowerInvariant() };
                if (!PeekWord("as"))
                {
                    var field = Field("a field to measure", allowDot: true);
                    var dot = field.IndexOf('.');
                    if (dot > 0)
                    {
                        measure.Via = field[..dot];
                        measure.Field = field[(dot + 1)..];
                    }
                    else
                    {
                        measure.Field = field;
                    }
                }

                if (!PeekWord("as")) throw new QueryTextException("A figure is named with 'as', for example 'sum Total as Revenue'.");
                _i++;

                var alias = Next("a name for the figure");
                if (alias.Kind != Kind.Word || !IsNamePart(alias.Text) || !char.IsAsciiLetter(alias.Text[0]) || Reserved.Contains(alias.Text))
                    throw Bad(alias, "A figure's name starts with a letter and is letters, digits and underscores");

                measure.As = alias.Text;
                definition.Measures.Add(measure);
            }
            while (Comma());
        }

        private bool Comma()
        {
            if (Peek is { Kind: Kind.Comma })
            {
                _i++;
                return true;
            }

            return false;
        }

        private void Order(QueryDefinition definition)
        {
            if (!PeekWord("by")) throw new QueryTextException("'order' is followed by 'by'.");
            _i++;

            definition.SortField = Field("a field to order by", allowDot: false);
            if (PeekWord("desc"))
            {
                _i++;
                definition.Descending = true;
            }
            else if (PeekWord("asc"))
            {
                _i++;
            }
        }

        private void Select(QueryDefinition definition)
        {
            do
            {
                definition.Fields.Add(Field("a field to select", allowDot: true));
            }
            while (Comma());
        }

        private void Limit(QueryDefinition definition)
        {
            var n = Next("a number");
            if (n.Kind != Kind.Word || n.Quoted || n.Text.Length > 5 || !n.Text.All(char.IsAsciiDigit) || !int.TryParse(n.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit))
                throw Bad(n, "A limit is a whole number");
            if (limit is < 1 or > QueryDefinition.MaxLimit)
                throw Bad(n, $"A limit is between 1 and {QueryDefinition.MaxLimit}");

            definition.Limit = limit;
        }
    }
}
