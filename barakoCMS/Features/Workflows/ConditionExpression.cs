using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Core.Expressions;

namespace barakoCMS.Features.Workflows;

/// <summary>
/// Reads the <c>Condition</c> of a Conditional step. The old form, <c>{{x}} == y</c>, keeps its
/// meaning exactly. Anything else is an expression from the same evaluator the templates use.
/// </summary>
/// <remarks>
/// A condition that is a single placeholder, then <c>==</c> or <c>!=</c>, then a plain value is the
/// old form, and it is compared as it always was: case-insensitive, quotes trimmed, <c>data.X</c>
/// read as stored. Every other condition is an expression, with <c>data.X</c>, <c>status</c>,
/// <c>contentType</c>, <c>id</c>, <c>createdAt</c>, <c>updatedAt</c> and <c>actions.N.name</c> as
/// names. A placeholder written around a plain name, <c>{{data.X}}</c>, is read as the name.
///
/// A name is replaced by one of its own that holds the value before the evaluator reads the text,
/// so a value is never part of the formula. A step output holding a quote or <c>{{...}}</c> is
/// text to compare, nothing else. A step output that is a plain number is a number, so that
/// <c>actions.1.count &gt; 3</c> works; every other output is text.
///
/// A condition that cannot be read or evaluated is false, and the note says why without any value.
/// </remarks>
internal static partial class ConditionExpression
{
    private const string Synthetic = "__v";

    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "true", "false", "null",
    };

    /// <summary>What a condition came to. <see cref="Note"/> is set when it could not be evaluated, and never holds a value.</summary>
    internal readonly record struct Verdict(bool Result, string? Note);

    /// <summary>A condition read and checked, waiting for a clock.</summary>
    internal sealed record Prepared(CheckedExpression? Expression, Bindings? Bindings, bool? Legacy, string? Note)
    {
        public bool UsesClock => Expression?.UsesClock ?? false;
    }

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"|\\{\\{\\s*(?<name>[A-Za-z_][A-Za-z0-9_.]*)\\s*\\}\\}", RegexOptions.None, 1000)]
    private static partial Regex Hole();

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"|(?<![A-Za-z0-9_.])actions\\.(?<step>[0-9]+)\\.", RegexOptions.None, 1000)]
    private static partial Regex StepOrdinal();

    [GeneratedRegex(@"^-?(0|[1-9][0-9]{0,17})(\.[0-9]{1,10})?\z", RegexOptions.None, 1000)]
    private static partial Regex PlainNumber();

    [GeneratedRegex(@"(^|[^A-Za-z0-9_])(and|or|not)([^A-Za-z0-9_]|$)|[()<>=!]", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Operator();

    /// <summary>Whether the condition reads the entry itself, which an erased entry no longer has.</summary>
    public static bool ReadsTheEntry(string condition)
    {
        if (condition.Contains("{{data.") || condition.Contains("{{status}}")) return true;

        var tokens = new List<Token>();
        var read = StepOrdinal().Replace(condition, m => m.Groups["step"].Success ? $"actions._{m.Groups["step"].Value}." : m.Value);
        if (Lexer.TryLex(read, tokens) is not null) return false;

        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.Name
                && token.Text is "data" or "status" or "createdAt" or "updatedAt")
            {
                return true;
            }
        }

        return false;
    }

    public static Prepared Prepare(string condition, barakoCMS.Models.Content content, IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> outputs)
    {
        try
        {
            if (TryLegacy(condition, content, out var legacy)) return new Prepared(null, null, legacy, null);

            var bindings = new Bindings(content, outputs);
            var named = Hole().Replace(condition, m => m.Groups["name"].Success ? m.Groups["name"].Value : m.Value);
            var rewritten = bindings.Rewrite(StepOrdinal().Replace(named, m => m.Groups["step"].Success ? $"actions._{m.Groups["step"].Value}." : m.Value));
            if (rewritten is null) return Failed(bindings.Error);

            var parsed = ExpressionEngine.ParseAndCheck(rewritten, bindings.TypeOf, refuseClock: false);
            return parsed.Expression is null
                ? Failed($"The condition could not be evaluated: {Describe(parsed.Errors.FirstOrDefault())}.")
                : new Prepared(parsed.Expression, bindings, null, null);
        }
        catch (RegexMatchTimeoutException)
        {
            return Failed("The condition took too long to read.");
        }
    }

    public static Verdict Run(Prepared prepared, TimeZoneInfo? zone)
    {
        if (prepared.Legacy is { } legacy) return new Verdict(legacy, null);
        if (prepared.Expression is null || prepared.Bindings is null) return new Verdict(false, prepared.Note);

        var clock = new ZonedClock(zone ?? TimeZoneInfo.Utc);
        var result = ExpressionEngine.Evaluate(prepared.Expression, prepared.Bindings.Read, clock);
        if (result.Note is not null) return new Verdict(false, $"The condition gave no value: {Describe(result.Note)}.");

        return result.Value is bool answer
            ? new Verdict(answer, null)
            : new Verdict(false, "The condition did not give true or false.");
    }

    private static Prepared Failed(string? note) => new(null, null, null, note ?? "The condition could not be read.");

    private static string Describe(ExpressionError? error) =>
        error is null ? "it is not valid" : $"{error.Message} (rule {error.Rule}, position {error.Position})";

    /// <summary>The old form, compared as it always was. False when the condition is not that form.</summary>
    private static bool TryLegacy(string condition, barakoCMS.Models.Content content, out bool result)
    {
        result = false;
        var parts = condition.Split(new[] { "==", "!=" }, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;

        var variable = parts[0].Trim();
        var expected = parts[1].Trim().Trim('"');
        var quoted = parts[1].Trim().Length >= 2 && parts[1].Trim()[0] == '"' && parts[1].Trim()[^1] == '"';
        if (!variable.StartsWith("{{", StringComparison.Ordinal) || !variable.EndsWith("}}", StringComparison.Ordinal)) return false;
        if (variable.IndexOf("{{", 2, StringComparison.Ordinal) >= 0 || variable.IndexOf("}}", StringComparison.Ordinal) != variable.Length - 2) return false;
        if (!quoted && Operator().IsMatch(expected)) return false;

        var actual = "";
        if (variable.Contains("{{data."))
        {
            var fieldName = variable.Replace("{{data.", "").Replace("}}", "");
            actual = content.Data.GetValueOrDefault(fieldName)?.ToString() ?? "";
        }
        else if (variable.Contains("{{status}}"))
        {
            actual = content.Status.ToString();
        }
        else if (variable.Contains("{{contentType}}"))
        {
            actual = content.ContentType;
        }

        var same = actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        result = condition.Contains("!=") ? !same : same;
        return true;
    }

    private sealed class ZonedClock(TimeZoneInfo zone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    /// <summary>The names one condition reads, each bound to a value of its own.</summary>
    internal sealed class Bindings(barakoCMS.Models.Content content, IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> outputs)
    {
        private readonly List<(object? Value, ExprType Type)> _bound = [];

        public string? Error { get; private set; }

        public ExprType? TypeOf(string field) =>
            field.StartsWith(Synthetic, StringComparison.Ordinal)
            && int.TryParse(field.AsSpan(Synthetic.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var at)
            && at < _bound.Count ? _bound[at].Type : null;

        public object? Read(NameRef name) =>
            name.Name.StartsWith(Synthetic, StringComparison.Ordinal)
            && int.TryParse(name.Name.AsSpan(Synthetic.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var at)
            && at < _bound.Count ? _bound[at].Value : null;

        /// <summary>The condition with each name replaced by a bound one, or null with <see cref="Error"/> set.</summary>
        public string? Rewrite(string source)
        {
            var tokens = new List<Token>();
            if (Lexer.TryLex(source, tokens) is { } lexError)
            {
                Error = $"The condition could not be read: {Describe(lexError)}.";
                return null;
            }

            var output = new StringBuilder(source.Length + 16);
            var at = 0;
            while (tokens[at].Kind != TokenKind.End)
            {
                var token = tokens[at];
                if (token.Kind == TokenKind.Equal && tokens[at + 1].Kind == TokenKind.Equal && tokens[at + 1].Position == token.Position + 1)
                {
                    // The old form is written with two, so a condition may be too.
                    output.Append("= ");
                    at += 2;
                    continue;
                }

                if (token.Kind != TokenKind.Name || Words.Contains(token.Text) || tokens[at + 1].Kind == TokenKind.LeftParen)
                {
                    output.Append(source, token.Position, tokens[at + 1].Position - token.Position);
                    at++;
                    continue;
                }

                var path = new List<string> { token.Text };
                var end = at + 1;
                while (tokens[end].Kind == TokenKind.Dot && tokens[end + 1].Kind == TokenKind.Name)
                {
                    path.Add(tokens[end + 1].Text);
                    end += 2;
                }

                if (!Bind(path, out var index)) return null;

                output.Append(Synthetic).Append(index.ToString(CultureInfo.InvariantCulture)).Append(' ');
                at = end;
            }

            return output.ToString();
        }

        private bool Bind(List<string> path, out int index)
        {
            index = -1;
            if (path.Any(part => part.StartsWith("__", StringComparison.Ordinal)))
            {
                Error = "The condition names something that starts with two underscores, and that is not allowed.";
                return false;
            }

            if (!Resolve(path, out var value, out var type))
            {
                Error = "The condition names something it cannot read.";
                return false;
            }

            _bound.Add((value, type));
            index = _bound.Count - 1;
            return true;
        }

        private bool Resolve(List<string> path, out object? value, out ExprType type)
        {
            value = null;
            type = ExprType.Text;

            if (path.Count == 1)
            {
                switch (path[0].ToLowerInvariant())
                {
                    case "status": value = content.Status.ToString(); return true;
                    case "contenttype": value = content.ContentType; return true;
                    case "id": value = content.Id.ToString(); return true;
                    case "createdat": value = content.CreatedAt; type = ExprType.Date; return true;
                    case "updatedat": value = content.UpdatedAt; type = ExprType.Date; return true;
                    default: return false;
                }
            }

            if (path.Count == 2 && path[0] == "data") return Field(path[1], out value, out type);
            if (path.Count == 3 && path[0] == "actions" && path[1].StartsWith('_') && int.TryParse(path[1].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
            {
                value = Output(ordinal, path[2], out type);
                return true;
            }

            return false;
        }

        private bool Field(string name, out object? value, out ExprType type)
        {
            value = null;
            type = ExprType.Text;
            if (content.Data is null) return true;

            foreach (var (key, raw) in content.Data)
            {
                if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) continue;

                var unwrapped = raw is JsonElement element ? FromJson(element) : raw;
                if (!Values.TryNormalize(unwrapped, out value)) return false;

                type = Values.TypeOf(value) is var kind && kind != ExprType.Null ? kind : ExprType.Text;
                return true;
            }

            return true;
        }

        private object? Output(int ordinal, string name, out ExprType type)
        {
            type = ExprType.Text;
            if (!outputs.TryGetValue(ordinal, out var step) || !step.TryGetValue(name, out var text)) return null;

            if (PlainNumber().IsMatch(text) && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            {
                type = ExprType.Number;
                return number;
            }

            return text;
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
}
