using System.Text.RegularExpressions;

namespace barakoCMS.Models;

/// <summary>The forms a <c>{{...}}</c> takes.</summary>
public enum HoleForm
{
    /// <summary><c>{{name}}</c>.</summary>
    Plain,

    /// <summary><c>{{name | format "argument"}}</c>.</summary>
    Formatted,

    /// <summary><c>{{= expression}}</c>, with or without a format after the expression.</summary>
    Expression,

    /// <summary><c>{{#each name}}</c>, over a reference field or a dataset.</summary>
    LoopOpen,

    /// <summary><c>{{/each}}</c>.</summary>
    LoopClose,

    /// <summary>Anything else: a duration, a link, or text that is not a placeholder.</summary>
    Other,
}

/// <summary>One <c>{{...}}</c> found in a template.</summary>
/// <param name="Form">Which form it is.</param>
/// <param name="Text">The hole as written, braces included.</param>
/// <param name="Reads">The names it reads, as written and once each. A call such as <c>sum</c> is not a name.</param>
public sealed record TemplateHole(HoleForm Form, string Text, IReadOnlyList<string> Reads);

/// <summary>
/// The <c>{{...}}</c> holes a template names, and the one rule every template that sends an entry
/// somewhere shares: a field that is not Public cannot leave.
/// </summary>
/// <remarks>
/// Outbound requests and generated documents both call this, so the two cannot drift apart on what
/// may leave. Each caller words its own refusal. <see cref="Scan"/> is the one scanner: it finds
/// every form, including an expression, a loop and a row field, and says what each one reads.
/// </remarks>
public static class TemplateHoles
{
    /// <summary>The longest text the scanner reads. A longer one is refused, never read in part.</summary>
    public const int MaxScanLength = 262_144;

    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(2);

    internal static readonly Regex Hole = new(
        @"\{\{\s*([A-Za-z0-9_.\[\]]+)\s*\}\}", RegexOptions.Compiled, ScanTimeout);

    private static readonly Regex Token = new(@"\{\{([^{}]*)\}\}", RegexOptions.Compiled, ScanTimeout);

    private static readonly Regex NameOnly = new(@"\A[A-Za-z0-9_.\[\]]+\z", RegexOptions.Compiled, ScanTimeout);

    private static readonly Regex LoopOpen = new(
        @"\A\#each\s+(?<name>[A-Za-z][A-Za-z0-9_.]*)\z", RegexOptions.Compiled, ScanTimeout);

    /// <summary>The hole name without a leading <c>data.</c>. Substitution and the Public check both use it.</summary>
    internal static string Bare(string name) =>
        name.StartsWith("data.", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;

    /// <summary>
    /// Every hole in <paramref name="template"/>, in order, whatever its form. The whole template is
    /// read before anything is returned.
    /// </summary>
    /// <exception cref="TemplateScanException">The template is over <see cref="MaxScanLength"/> or the scan timed out. It is never read in part.</exception>
    public static IReadOnlyList<TemplateHole> Scan(string? template)
    {
        if (string.IsNullOrEmpty(template)) return [];

        Guard(template);
        try
        {
            return Token.Matches(template).Select(m => Classify(m.Value, m.Groups[1].Value)).ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            throw TemplateScanException.Slow();
        }
    }

    /// <summary>
    /// A test sets this on its own flow to make every scan time out. Every pattern here is linear, so
    /// no template under the cap takes two seconds, and this is the only way to prove what a
    /// timeout does to each caller.
    /// </summary>
    internal static readonly AsyncLocal<bool> TimeOutScans = new();

    /// <summary>Refuses a template over <see cref="MaxScanLength"/>, so no check reads a template in part.</summary>
    /// <exception cref="TemplateScanException">The template is too long.</exception>
    public static void Guard(string? template)
    {
        if (template is not null && template.Length > MaxScanLength) throw TemplateScanException.Long();
        if (TimeOutScans.Value) throw TemplateScanException.Slow();
    }

    private static TemplateHole Classify(string whole, string body)
    {
        var trimmed = body.Trim();

        if (trimmed.StartsWith('='))
        {
            return new TemplateHole(HoleForm.Expression, whole, Paths(trimmed[1..]));
        }

        if (trimmed == "/each") return new TemplateHole(HoleForm.LoopClose, whole, []);

        if (LoopOpen.Match(trimmed) is { Success: true } open)
        {
            return new TemplateHole(HoleForm.LoopOpen, whole, [open.Groups["name"].Value]);
        }

        if (NameOnly.IsMatch(trimmed)) return new TemplateHole(HoleForm.Plain, whole, [trimmed]);

        var reads = Paths(trimmed);
        var bar = FirstBar(trimmed);
        if (bar > 0 && NameOnly.IsMatch(trimmed[..bar].Trim()))
        {
            return new TemplateHole(HoleForm.Formatted, whole, [trimmed[..bar].Trim()]);
        }

        return new TemplateHole(HoleForm.Other, whole, reads);
    }

    /// <summary>Where the first <c>|</c> outside a string literal is, or -1. Where an expression ends and its format begins.</summary>
    public static int FirstBar(string text)
    {
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') quoted = !quoted;
            else if (text[i] == '|' && !quoted) return i;
        }

        return -1;
    }

    /// <summary>
    /// The dotted names in an expression, outside its string literals. A name followed by an opening
    /// parenthesis is a function and is left out, and so is a number.
    /// </summary>
    internal static IReadOnlyList<string> Paths(string source)
    {
        var found = new List<string>();
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '"')
            {
                i = EndOfLiteral(source, i);
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_' || source[i] == '.'))
                {
                    i++;
                }

                var name = source[start..i].TrimEnd('.');
                var next = i;
                while (next < source.Length && char.IsWhiteSpace(source[next]))
                {
                    next++;
                }

                var call = next < source.Length && source[next] == '(';
                if (!call && !found.Contains(name, StringComparer.Ordinal)) found.Add(name);
            }
            else if (char.IsDigit(c))
            {
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '.'))
                {
                    i++;
                }
            }
            else
            {
                i++;
            }
        }

        return found;
    }

    private static int EndOfLiteral(string source, int open)
    {
        var i = open + 1;
        while (i < source.Length)
        {
            if (source[i] == '\\') i += 2;
            else if (source[i++] == '"') break;
        }

        return i;
    }

    /// <summary>
    /// Replaces each plain <c>{{name}}</c> with what <paramref name="value"/> gives, or leaves it as
    /// written when that is null. Every other form is left as written.
    /// </summary>
    internal static string Replace(string template, Func<string, string?> value)
    {
        Guard(template);
        try
        {
            return Hole.Replace(template, match => value(match.Groups[1].Value) ?? match.Value);
        }
        catch (RegexMatchTimeoutException)
        {
            throw TemplateScanException.Slow();
        }
    }

    /// <summary>The name inside each plain or formatted hole of <paramref name="template"/>, in order.</summary>
    public static IEnumerable<string> Names(string? template) =>
        Scan(template).Where(h => h.Form is HoleForm.Plain or HoleForm.Formatted).SelectMany(h => h.Reads);

    /// <summary>The schema field a hole names, or null when it names a system variable or nothing.</summary>
    public static FieldDefinition? FieldFor(string name, ContentTypeDefinition schema)
    {
        // "data.Title" and "Title" both name the same field: the admin's variable picker offers the
        // first and templates in the wild use the second.
        var bare = Bare(name);

        return schema.Fields.FirstOrDefault(f => string.Equals(f.Name, bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The field an entry read names, following a reference one step: <c>data.Customer.Name</c>
    /// reads <c>Customer</c> on this entry. Null for a name that is not a field.
    /// </summary>
    public static FieldDefinition? FieldRead(string name, ContentTypeDefinition schema)
    {
        var first = Bare(name).Split('.', 2)[0];
        return schema.Fields.FirstOrDefault(f => string.Equals(f.Name, first, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The first hole in <paramref name="templates"/>, in any form, that reads a field of
    /// <paramref name="schema"/> whose sensitivity is not Public, or null when there is none.
    /// </summary>
    public static (string Hole, FieldDefinition Field)? FirstNonPublic(
        IEnumerable<string?> templates, ContentTypeDefinition schema)
    {
        foreach (var template in templates)
        {
            foreach (var hole in Scan(template))
            {
                foreach (var read in hole.Reads)
                {
                    var field = FieldRead(read, schema);
                    if (field is not null && (field.Sensitivity != SensitivityLevel.Public || !string.IsNullOrEmpty(field.ShownWhenListedIn)))
                    {
                        return (hole.Text, field);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The first hole that is an expression, a loop, or a name from a dataset row. A caller that
    /// cannot render these refuses the template, and does not send them as written.
    /// </summary>
    public static string? FirstBeyondPlain(IEnumerable<string?> templates)
    {
        foreach (var template in templates)
        {
            foreach (var hole in Scan(template))
            {
                var row = hole.Reads.Any(r => r.StartsWith("row.", StringComparison.OrdinalIgnoreCase));
                if (hole.Form is HoleForm.Expression or HoleForm.LoopOpen or HoleForm.LoopClose || row) return hole.Text;
            }
        }

        return null;
    }
}

/// <summary>A template that cannot be checked: it is over the length cap, or reading it timed out.</summary>
/// <remarks>
/// A check that cannot finish refuses. It never reads a template in part and never counts a timeout
/// as "no holes". The message names the limit and never any of the template.
/// </remarks>
public sealed class TemplateScanException : Exception
{
    private TemplateScanException(string message, bool timedOut)
        : base(message)
    {
        TimedOut = timedOut;
    }

    /// <summary>True when the scan timed out, false when the template was over the length cap.</summary>
    public bool TimedOut { get; }

    internal static TemplateScanException Long() =>
        new($"A template may be at most {TemplateHoles.MaxScanLength} characters.", false);

    internal static TemplateScanException Slow() =>
        new("The template took too long to read, so it was refused.", true);
}
