using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Sync;

/// <summary>
/// Evaluates a sync's field rules and exclusions against one flattened source item.
/// </summary>
/// <remarks>
/// Static and pure over the rows <see cref="SyncPayloadReader"/> produces, for the same reason that
/// reader is: what a rule makes of an item can be decided without a network or a database.
///
/// An array path, <c>labels[].name</c>, is matched against the flattened keys the reader writes
/// (<c>labels[0].name</c>, <c>labels[1].name</c>), in index order, so the reader did not have to
/// change and a plain field map reads exactly what it read before.
/// </remarks>
internal static class SyncRules
{
    private const string ArrayMarker = "[]";

    /// <summary>The longest regex a rule may carry.</summary>
    public const int MaxRegexLength = 500;

    /// <summary>
    /// Patterns are compiled once per process. Bounded, because the patterns come from every
    /// tenant's saved syncs and a cache that only grows is a slow leak.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex> Compiled = new(StringComparer.Ordinal);
    private const int MaxCompiled = 256;

    public static bool IsArrayPath(string? path) => path is not null && path.Contains(ArrayMarker, StringComparison.Ordinal);

    /// <summary>Why this rule cannot be saved, or null. Checks only its own shape.</summary>
    public static string? ShapeProblem(string field, SyncFieldRule? rule)
    {
        if (rule is null)
        {
            return $"FieldRules '{field}' needs exactly one of const, path or ratio.";
        }

        var sources = (rule.Const is not null ? 1 : 0)
                    + (!string.IsNullOrWhiteSpace(rule.Path) ? 1 : 0)
                    + (rule.Ratio is not null ? 1 : 0);

        if (sources != 1)
        {
            return $"FieldRules '{field}' needs exactly one of const, path or ratio.";
        }

        var transforms = rule.PrefixStrip is not null || rule.Regex is not null
                      || rule.Join is not null || rule.Contains is not null;

        if (string.IsNullOrWhiteSpace(rule.Path) && transforms)
        {
            return $"FieldRules '{field}': prefixStrip, regex, join and contains only apply to a path.";
        }

        if (rule.Join is not null && rule.Contains is not null)
        {
            return $"FieldRules '{field}' takes join or contains, not both.";
        }

        if (rule.Ratio is not null
            && (rule.Ratio.Count != 2 || rule.Ratio.Any(string.IsNullOrWhiteSpace)))
        {
            return $"FieldRules '{field}' ratio needs exactly two paths: closed, then open.";
        }

        if (rule.Regex is not null)
        {
            if (rule.Regex.Length is 0 or > MaxRegexLength)
            {
                return $"FieldRules '{field}' regex must be 1 to {MaxRegexLength} characters.";
            }

            Regex compiled;
            try
            {
                compiled = Build(rule.Regex);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return $"FieldRules '{field}' has a regex that does not compile: {ex.Message}";
            }

            // Group 0 is the whole match, so one capture group means two group numbers.
            if (compiled.GetGroupNumbers().Length != 2)
            {
                return $"FieldRules '{field}' regex must have exactly one capture group, which is the value written.";
            }
        }

        return null;
    }

    /// <summary>Why this exclusion cannot be saved, or null.</summary>
    public static string? ShapeProblem(SyncExcludeRule? rule) =>
        rule is null
        || string.IsNullOrWhiteSpace(rule.Path)
        || rule.NotEmpty == (rule.EqualTo is not null)
            ? "Every exclude rule needs a path and exactly one of notEmpty or equalTo."
            : null;

    /// <summary>Every path a rule reads, for the checks that depend on the source.</summary>
    public static IEnumerable<string> PathsOf(SyncFieldRule rule) =>
        rule.Ratio ?? (rule.Path is { } path ? [path] : []);

    /// <summary>Does any exclusion match this item?</summary>
    public static bool Excluded(IReadOnlyList<SyncExcludeRule> rules, IReadOnlyDictionary<string, string> row)
    {
        foreach (var rule in rules)
        {
            if (rule.EqualTo is { } wanted)
            {
                if (Values(row, rule.Path).Any(v => string.Equals(v, wanted, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            else if (rule.NotEmpty && Present(row, rule.Path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What a rule makes of one item: a string, a list of strings for an array path with no join or
    /// contains, or null when there is nothing to write.
    /// </summary>
    public static object? Evaluate(SyncFieldRule rule, IReadOnlyDictionary<string, string> row)
    {
        if (rule.Const is { } constant) return constant;

        if (rule.Ratio is [var closedPath, var openPath])
        {
            return Ratio(row, closedPath, openPath);
        }

        if (string.IsNullOrWhiteSpace(rule.Path)) return null;

        var values = Values(row, rule.Path).Select(v => Transform(rule, v)).OfType<string>().ToList();

        if (rule.Contains is { } wanted)
        {
            // An item with no labels contains nothing, which is an answer, so it writes false rather
            // than leaving the field out.
            return values.Any(v => string.Equals(v, wanted, StringComparison.OrdinalIgnoreCase)) ? "true" : "false";
        }

        if (values.Count == 0) return null;

        if (rule.Join is { } separator) return string.Join(separator, values);

        return IsArrayPath(rule.Path) ? values : values[0];
    }

    private static string? Transform(SyncFieldRule rule, string value)
    {
        if (rule.PrefixStrip is { Length: > 0 } prefix && value.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = value[prefix.Length..];
        }

        if (rule.Regex is { } pattern)
        {
            var match = Build(pattern).Match(value);
            if (!match.Success) return null;

            var group = match.Groups.Values.Skip(1).FirstOrDefault(g => g.Success);
            if (group is null) return null;
            value = group.Value;
        }

        return value.Length == 0 ? null : value;
    }

    private static string? Ratio(IReadOnlyDictionary<string, string> row, string closedPath, string openPath)
    {
        if (!TryNumber(row, closedPath, out var closed) || !TryNumber(row, openPath, out var open)) return null;

        var total = closed + open;
        var percent = total == 0 ? 0 : Math.Round(closed * 100 / total, MidpointRounding.AwayFromZero);

        return percent.ToString("0", CultureInfo.InvariantCulture);
    }

    private static bool TryNumber(IReadOnlyDictionary<string, string> row, string path, out decimal number)
    {
        number = 0;
        return row.TryGetValue(path, out var text)
            && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>The non-empty values a path names in this item, in array order.</summary>
    public static IReadOnlyList<string> Values(IReadOnlyDictionary<string, string> row, string path)
    {
        if (!IsArrayPath(path))
        {
            return row.TryGetValue(path, out var single) && single.Length > 0 ? [single] : [];
        }

        var matcher = Build("^" + Regex.Escape(path).Replace(@"\[]", @"\[(\d+)]", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase);

        return row
            .Select(kv => (kv.Value, Match: matcher.Match(kv.Key)))
            .Where(x => x.Match.Success && x.Value.Length > 0)
            .OrderBy(x => x.Match, IndexOrder.Instance)
            .Select(x => x.Value)
            .ToList();
    }

    /// <summary>
    /// Does the path hold anything? The flattener writes no key for an empty array or object, so a
    /// path with keys beneath it is non-empty.
    /// </summary>
    private static bool Present(IReadOnlyDictionary<string, string> row, string path) =>
        Values(row, path).Count > 0
        || (!IsArrayPath(path) && row.Keys.Any(k =>
            k.StartsWith(path + "[", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase)));

    private static Regex Build(string pattern, RegexOptions extra = RegexOptions.None)
    {
        var key = $"{(int)extra}:{pattern}";
        if (Compiled.TryGetValue(key, out var cached)) return cached;

        // NonBacktracking runs in time linear in the input, so a pattern an operator saved cannot
        // stall the sweep on a hostile or unlucky value.
        var regex = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | extra);

        if (Compiled.Count >= MaxCompiled) Compiled.Clear();
        Compiled[key] = regex;
        return regex;
    }

    /// <summary>Orders array-path matches by their indices, numerically and outermost first.</summary>
    private sealed class IndexOrder : IComparer<Match>
    {
        public static readonly IndexOrder Instance = new();

        public int Compare(Match? x, Match? y)
        {
            for (var i = 1; i < x!.Groups.Count && i < y!.Groups.Count; i++)
            {
                var order = int.Parse(x.Groups[i].Value, CultureInfo.InvariantCulture)
                    .CompareTo(int.Parse(y.Groups[i].Value, CultureInfo.InvariantCulture));
                if (order != 0) return order;
            }

            return 0;
        }
    }
}
