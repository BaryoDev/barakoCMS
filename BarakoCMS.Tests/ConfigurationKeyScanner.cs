using System.Text;
using System.Text.RegularExpressions;

namespace BarakoCMS.Tests;

/// <summary>
/// Finds the configuration keys the shipped source reads, by scanning the C# text. It needs no
/// host and no database. Used by <see cref="ConfigurationReferenceTests"/>.
/// </summary>
/// <remarks>
/// What it sees: a string literal, a string constant, a concatenation of those, an interpolated
/// string, or nameof(...), passed as the first argument of GetSection, GetRequiredSection,
/// GetConnectionString, BindConfiguration, GetValue, GetValue&lt;T&gt;, an indexer on a configuration
/// object, or Environment.GetEnvironmentVariable. An interpolated hole it cannot resolve becomes a
/// "*", so a key built at run time is reported as a pattern. A read whose key is wholly a variable
/// is reported as unresolved, and the test lists the files where that is expected.
/// </remarks>
internal static class ConfigurationKeyScanner
{
    internal enum ReadKind
    {
        /// <summary>A key under the configuration root, possibly with "*" for a run-time part.</summary>
        Key,
        /// <summary>A section under the root, read with GetSection or BindConfiguration.</summary>
        Prefix,
        /// <summary>A key under a section the file was handed (a module's own section), so only its tail is known.</summary>
        Leaf,
        /// <summary>An environment variable.</summary>
        Env,
    }

    internal sealed record Read(string File, int Line, ReadKind Kind, string Key);

    internal sealed record Unresolved(string File, int Line, string Expression);

    internal sealed record Result(IReadOnlyList<Read> Reads, IReadOnlyList<Unresolved> Unresolved, IReadOnlyList<string> Files);

    // Project folders that are not shipped.
    private static readonly Regex NotShipped = new(
        "Tests|Testing|Templates|tests-fixtures|Benchmark|Sample|aspire", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProjectLine = new(
        "^Project\\(\"[^\"]*\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"(?<path>[^\"]+\\.csproj)\"", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex CallName = new(
        @"\b(?<m>GetRequiredSection|GetSection|GetConnectionString|BindConfiguration|GetValue(?:\s*<[^<>()]*(?:<[^<>()]*>[^<>()]*)*>)?|GetEnvironmentVariable)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex Indexer = new(
        @"(?<![\w\.])(?<r>(?:[A-Za-z_]\w*\.)*(?:_?config(?:uration)?|cfg|section|adminConfig))\s*\??\s*\[",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // An environment variable read through a delegate: environment("X") or (a ?? Environment.GetEnvironmentVariable)("X").
    private static readonly Regex EnvLiteral = new(
        "(?:\\benvironment|GetEnvironmentVariable\\s*\\))\\s*\\(\\s*\"(?<n>[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)*)\"", RegexOptions.Compiled);

    // A constant shaped like an environment variable name (BARAKO_SECRETS_DIR) is one the code reads.
    private static readonly Regex EnvShaped = new("^[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+$", RegexOptions.Compiled);

    private static readonly Regex ConstDecl = new(
        @"\bconst\s+string\s+(?<n>\w+)\s*=\s*(?<v>[^;]+);", RegexOptions.Compiled);

    private static readonly Regex TypeDecl = new(
        @"\b(?:class|record|struct|interface)\s+(?<n>\w+)", RegexOptions.Compiled);

    private static readonly HashSet<string> ConfigLikeReceivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "_config", "configuration", "_configuration", "cfg", "section", "adminConfig", "root",
    };

    internal static Result Scan(string repoRoot, ISet<string> moduleSectionFiles)
    {
        var files = ShippedFiles(repoRoot);
        var texts = files.ToDictionary(f => f, f => StripComments(File.ReadAllText(Path.Combine(repoRoot, f))));
        var consts = CollectConsts(texts);

        var reads = new List<Read>();
        var unresolved = new List<Unresolved>();

        foreach (var (file, text) in texts)
        {
            var lineStarts = LineStarts(text);
            var moduleRelative = moduleSectionFiles.Contains(file);

            foreach (Match m in CallName.Matches(text))
            {
                var name = m.Groups["m"].Value;
                var generic = name.StartsWith("GetValue", StringComparison.Ordinal) && name.Contains('<');
                var bare = generic ? "GetValue" : name;
                var receiver = ReceiverBefore(text, m.Index);

                if (bare == "GetEnvironmentVariable")
                {
                    AddRead(reads, unresolved, file, LineOf(lineStarts, m.Index), ReadKind.Env, FirstArgument(text, m.Index + m.Length), consts, file);
                    continue;
                }

                // GetValue also exists on readers, reflection and JSON nodes; the generic form is
                // configuration's, the plain one counts only on something named like configuration.
                if (bare == "GetValue" && !generic && !ConfigLikeReceivers.Contains(LastSegment(receiver)))
                    continue;

                var relative = moduleRelative || IsRelativeReceiver(receiver);
                var kind = relative
                    ? ReadKind.Leaf
                    : bare is "GetSection" or "GetRequiredSection" or "BindConfiguration" ? ReadKind.Prefix : ReadKind.Key;
                var arg = FirstArgument(text, m.Index + m.Length);
                if (bare == "GetConnectionString")
                {
                    var key = Resolve(arg, consts, file);
                    if (key is not null && IsKey(key))
                        reads.Add(new Read(file, LineOf(lineStarts, m.Index), ReadKind.Key, "ConnectionStrings:" + key));
                    else if (arg.Length > 0)
                        unresolved.Add(new Unresolved(file, LineOf(lineStarts, m.Index), arg));
                    continue;
                }

                AddRead(reads, unresolved, file, LineOf(lineStarts, m.Index), kind, arg, consts, file);
            }

            foreach (Match m in EnvLiteral.Matches(text))
                reads.Add(new Read(file, LineOf(lineStarts, m.Index), ReadKind.Env, m.Groups["n"].Value));

            foreach (Match m in ConstDecl.Matches(text))
            {
                var literal = m.Groups["v"].Value.Trim();
                if (literal.Length > 2 && literal[0] == '"' && literal[^1] == '"' && EnvShaped.IsMatch(literal[1..^1])
                    && literal != "\"REPLACE_THIS\"")
                    reads.Add(new Read(file, LineOf(lineStarts, m.Index), ReadKind.Env, literal[1..^1]));
            }

            foreach (Match m in Indexer.Matches(text))
            {
                var receiver = m.Groups["r"].Value;
                var relative = moduleRelative || IsRelativeReceiver(receiver);
                var open = m.Index + m.Length;
                var arg = ArgumentUntil(text, open, ']');
                AddRead(reads, unresolved, file, LineOf(lineStarts, m.Index), relative ? ReadKind.Leaf : ReadKind.Key, arg, consts, file);
            }
        }

        return new Result(reads, unresolved, files);
    }

    /// <summary>
    /// The keys an options class binds from <paramref name="prefix"/>: each public property with a
    /// setter, one level of our own nested option classes down. Scalars, enums, time spans, lists and
    /// dictionaries are single keys.
    /// </summary>
    internal static IReadOnlyList<string> ExpandBound(string prefix, Type type)
    {
        var keys = new List<string>();
        Expand(prefix, type, keys, 0);
        return keys;
    }

    private static void Expand(string prefix, Type type, List<string> keys, int depth)
    {
        foreach (var p in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (p.SetMethod is null || !p.SetMethod.IsPublic || p.GetIndexParameters().Length > 0)
                continue;

            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            var scalar = t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
                || t == typeof(TimeSpan) || t == typeof(Guid) || t == typeof(Uri)
                || typeof(System.Collections.IEnumerable).IsAssignableFrom(t);
            if (scalar)
                keys.Add(prefix + ":" + p.Name);
            else if (depth < 3 && t.Namespace is { } ns && ns.StartsWith("barakoCMS", StringComparison.OrdinalIgnoreCase))
                Expand(prefix + ":" + p.Name, t, keys, depth + 1);
        }
    }

    private static bool IsRelativeReceiver(string receiver)
    {
        var last = LastSegment(receiver);
        return last.Equals("section", StringComparison.OrdinalIgnoreCase)
            || last.Equals("adminConfig", StringComparison.OrdinalIgnoreCase)
            || last.Equals("rate", StringComparison.OrdinalIgnoreCase);
    }

    private static string LastSegment(string receiver)
    {
        var i = receiver.LastIndexOf('.');
        return i < 0 ? receiver : receiver[(i + 1)..];
    }

    private static void AddRead(
        List<Read> reads, List<Unresolved> unresolved, string file, int line, ReadKind kind,
        string arg, Dictionary<string, List<(string File, string Class, string Value)>> consts, string fromFile)
    {
        if (arg.Length == 0 || char.IsDigit(arg[0]))
            return;

        var key = Resolve(arg, consts, fromFile);
        if (key is null || !IsKey(key))
        {
            unresolved.Add(new Unresolved(file, line, arg));
            return;
        }

        if (kind == ReadKind.Env && key.Contains("__", StringComparison.Ordinal))
        {
            reads.Add(new Read(file, line, ReadKind.Key, key.Replace("__", ":", StringComparison.Ordinal)));
            return;
        }

        reads.Add(new Read(file, line, kind, key));
    }

    // A key is usable when it has some literal text. "*" alone, or a leading "*:" (the section
    // itself is a variable), tells nothing about which key is read.
    private static bool IsKey(string key) =>
        key.Length > 0 && key != "*" && !key.StartsWith("*:", StringComparison.Ordinal);

    internal static IReadOnlyList<string> ShippedFiles(string repoRoot)
    {
        var sln = File.ReadAllText(Path.Combine(repoRoot, "barakoCMS.sln"));
        var files = new List<string>();
        foreach (Match m in ProjectLine.Matches(sln))
        {
            var csproj = m.Groups["path"].Value.Replace('\\', '/');
            if (NotShipped.IsMatch(csproj))
                continue;

            var dir = Path.GetDirectoryName(csproj)!.Replace('\\', '/');
            var full = Path.Combine(repoRoot, dir);
            foreach (var path in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
                var parts = rel.Split('/');
                if (parts.Contains("bin") || parts.Contains("obj"))
                    continue;
                files.Add(rel);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static Dictionary<string, List<(string File, string Class, string Value)>> CollectConsts(
        Dictionary<string, string> texts)
    {
        var map = new Dictionary<string, List<(string File, string Class, string Value)>>(StringComparer.Ordinal);
        foreach (var (file, text) in texts)
        {
            var types = TypeDecl.Matches(text).Select(t => (t.Index, Name: t.Groups["n"].Value)).ToList();
            foreach (Match c in ConstDecl.Matches(text))
            {
                var owner = types.LastOrDefault(t => t.Index < c.Index).Name ?? "";
                var name = c.Groups["n"].Value;
                if (!map.TryGetValue(name, out var list))
                    map[name] = list = [];
                list.Add((file, owner, c.Groups["v"].Value.Trim()));
            }
        }

        return map;
    }

    /// <summary>The key an expression stands for, with "*" for a part only known at run time, or null.</summary>
    private static string? Resolve(
        string expression, Dictionary<string, List<(string File, string Class, string Value)>> consts, string file, int depth = 0)
    {
        if (depth > 6)
            return null;

        var parts = SplitTopLevel(expression.Trim(), '+');
        var sb = new StringBuilder();
        foreach (var raw in parts)
        {
            var p = raw.Trim();
            while (p.Length > 1 && p[0] == '(' && p[^1] == ')' && SplitTopLevel(p[1..^1], '+').Count >= 1 && Balanced(p[1..^1]))
                p = p[1..^1].Trim();

            if (p.Length >= 2 && p[0] == '"' && p[^1] == '"')
            {
                sb.Append(Unescape(p[1..^1]));
            }
            else if (p.Length >= 3 && p.StartsWith("$\"", StringComparison.Ordinal) && p[^1] == '"')
            {
                var inner = Interpolated(p[2..^1], consts, file, depth);
                if (inner is null)
                    return null;
                sb.Append(inner);
            }
            else if (p.StartsWith("nameof(", StringComparison.Ordinal) && p[^1] == ')')
            {
                sb.Append(LastSegment(p[7..^1].Trim()));
            }
            else if (Regex.IsMatch(p, @"^[A-Za-z_][\w\.]*$"))
            {
                var value = ConstValue(p, consts, file, depth);
                if (value is null)
                    return null;
                sb.Append(value);
            }
            else
            {
                return null;
            }
        }

        return sb.ToString();
    }

    private static string? Interpolated(
        string body, Dictionary<string, List<(string File, string Class, string Value)>> consts, string file, int depth)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < body.Length; i++)
        {
            var ch = body[i];
            if (ch == '{' && i + 1 < body.Length && body[i + 1] == '{')
            {
                sb.Append('{');
                i++;
            }
            else if (ch == '}' && i + 1 < body.Length && body[i + 1] == '}')
            {
                sb.Append('}');
                i++;
            }
            else if (ch == '{')
            {
                var close = body.IndexOf('}', i);
                if (close < 0)
                    return null;
                var hole = body[(i + 1)..close].Trim();
                var colon = hole.IndexOf(':');
                if (colon > 0)
                    hole = hole[..colon].Trim();
                var value = Regex.IsMatch(hole, @"^[A-Za-z_][\w\.]*$") ? ConstValue(hole, consts, file, depth) : null;
                sb.Append(value ?? "*");
                i = close;
            }
            else
            {
                sb.Append(ch);
            }
        }

        // Adjacent holes collapse, so {a}{b} is one "*".
        return Regex.Replace(Unescape(sb.ToString()), @"\*+", "*");
    }

    private static string? ConstValue(
        string name, Dictionary<string, List<(string File, string Class, string Value)>> consts, string file, int depth)
    {
        var segments = name.Split('.');
        var simple = segments[^1];
        var owner = segments.Length > 1 ? segments[^2] : null;
        if (!consts.TryGetValue(simple, out var list))
            return null;

        var candidates = owner is not null
            ? list.Where(c => c.Class == owner).ToList()
            : list.Where(c => c.File == file).ToList();
        if (candidates.Count == 0 && owner is null)
        {
            // Not declared in this file: take it only when every declaration agrees.
            var values = list.Select(c => Resolve(c.Value, consts, c.File, depth + 1)).Distinct().ToList();
            return values.Count == 1 ? values[0] : null;
        }

        if (candidates.Count == 0)
            return null;

        var resolved = candidates.Select(c => Resolve(c.Value, consts, c.File, depth + 1)).Distinct().ToList();
        return resolved.Count == 1 ? resolved[0] : null;
    }

    private static string Unescape(string s) => s.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    private static bool Balanced(string s)
    {
        var depth = 0;
        var inString = false;
        foreach (var c in s)
        {
            if (c == '"') inString = !inString;
            if (inString) continue;
            if (c == '(') depth++;
            if (c == ')' && --depth < 0) return false;
        }

        return depth == 0;
    }

    private static List<string> SplitTopLevel(string s, char sep)
    {
        var parts = new List<string>();
        var depth = 0;
        var inString = false;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"' && (i == 0 || s[i - 1] != '\\')) inString = !inString;
            if (inString) continue;
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
            else if (c == sep && depth == 0)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }

        parts.Add(s[start..]);
        return parts;
    }

    private static string FirstArgument(string text, int start) => ArgumentUntil(text, start, ')');

    // The text from start to the first top level comma or the closing character.
    private static string ArgumentUntil(string text, int start, char close)
    {
        var depth = 0;
        var inString = false;
        var i = start;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && text[i - 1] != '\\') inString = !inString;
            if (inString) continue;
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                    break;
                depth--;
            }
            else if (c == ',' && depth == 0 && close == ')')
                break;
        }

        return text[start..Math.Min(i, text.Length)].Trim();
    }

    private static string ReceiverBefore(string text, int callStart)
    {
        var i = callStart - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        if (i < 0 || text[i] != '.')
            return "";
        i--;
        if (i >= 0 && text[i] == '?') i--;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        var end = i;
        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.')) i--;
        return end < 0 || i == end ? "" : text[(i + 1)..(end + 1)];
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                starts.Add(i + 1);
        return starts;
    }

    private static int LineOf(List<int> starts, int index)
    {
        var i = starts.BinarySearch(index);
        return (i >= 0 ? i : ~i - 1) + 1;
    }

    /// <summary>
    /// The source with comments replaced by spaces, so line numbers hold. String, verbatim, raw and
    /// character literals are left alone, so a "//" inside a URL is not a comment.
    /// </summary>
    internal static string StripComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        var i = 0;
        while (i < src.Length)
        {
            var c = src[i];
            var next = i + 1 < src.Length ? src[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < src.Length && src[i] != '\n') { sb.Append(' '); i++; }
            }
            else if (c == '/' && next == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i < src.Length && !(src[i] == '*' && i + 1 < src.Length && src[i + 1] == '/'))
                {
                    sb.Append(src[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                if (i < src.Length) { sb.Append("  "); i += 2; }
            }
            else if (c == '"' && next == '"' && i + 2 < src.Length && src[i + 2] == '"')
            {
                // Raw string literal: runs to the next run of three or more quotes.
                var j = i;
                while (j < src.Length && src[j] == '"') j++;
                var quotes = j - i;
                var end = src.IndexOf(new string('"', quotes), j, StringComparison.Ordinal);
                end = end < 0 ? src.Length : end + quotes;
                sb.Append(src, i, end - i);
                i = end;
            }
            else if (c == '"')
            {
                var verbatim = i > 0 && (src[i - 1] == '@' || (i > 1 && src[i - 1] == '$' && src[i - 2] == '@') || (i > 1 && src[i - 1] == '@' && src[i - 2] == '$'));
                sb.Append(c);
                i++;
                while (i < src.Length)
                {
                    if (verbatim && src[i] == '"' && i + 1 < src.Length && src[i + 1] == '"') { sb.Append("\"\""); i += 2; continue; }
                    if (!verbatim && src[i] == '\\' && i + 1 < src.Length) { sb.Append(src[i]).Append(src[i + 1]); i += 2; continue; }
                    if (src[i] == '"') { sb.Append('"'); i++; break; }
                    sb.Append(src[i]);
                    i++;
                }
            }
            else if (c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < src.Length && src[i] != '\n')
                {
                    if (src[i] == '\\' && i + 1 < src.Length) { sb.Append(src[i]).Append(src[i + 1]); i += 2; continue; }
                    sb.Append(src[i]);
                    if (src[i++] == '\'') break;
                }
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }
}
