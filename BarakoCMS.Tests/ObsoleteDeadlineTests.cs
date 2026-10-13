using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// No <c>[Obsolete]</c> in the tree may name a removal deadline that the release being cut has
/// already reached.
/// </summary>
/// <remarks>
/// Four members promised removal "in the next major version" and were still there when that major
/// was being cut, because "the next major version" silently changes meaning every time a major
/// ships. Worse, their paired Deconstruct overloads said "barakoCMS 5.0", so removing the
/// constructors on schedule would have left half a break behind, which is what the comment above
/// them was written to prevent.
///
/// Two rules, both mechanical. A relative deadline is banned outright, since it cannot be checked.
/// An absolute one has to be at least one major beyond the version being built, which is what
/// CLAUDE.md section 6 asks for.
/// </remarks>
public class ObsoleteDeadlineTests
{
    // The attribute however it is written: [Obsolete], [System.Obsolete(...)], [ObsoleteAttribute(...)],
    // or one entry of a list such as [Serializable, Obsolete(...)]. Only a name that opens an
    // attribute list or follows a comma in one counts, so a comment or an identifier does not.
    private static readonly Regex ObsoleteAttribute = new(
        @"(?<=\[\s*|\[[^\[\]""]*,\s*)(?:global::)?(?:System\.)?Obsolete(?:Attribute)?\b(?!\s*[.<])");
    private static readonly Regex NamedVersion = new(@"barakoCMS (\d+)\.0");
    private static readonly Regex Removal = new(@"remov", RegexOptions.IgnoreCase);

    [Fact]
    public void No_obsolete_member_names_a_deadline_this_release_has_already_reached()
    {
        var currentMajor = typeof(barakoCMS.Data.DataSeeder).Assembly.GetName().Version!.Major;

        var offenders = SourceFiles()
            .SelectMany(file => Offenders(File.ReadAllText(file), currentMajor)
                .Select(problem => $"{Path.GetFileName(file)}: {problem}"))
            .ToList();

        // Every offender in the message: a collection assertion prints only the first ten.
        offenders.Should().BeEmpty(string.Join(Environment.NewLine, offenders));
    }

    [Theory]
    [InlineData("[Obsolete(\"Use B. Removal planned for barakoCMS 5.0.\")]")]
    [InlineData("[Obsolete(\"Use B. \"\n        + \"B is removed in barakoCMS 5.0.\")]")]
    [InlineData("[Obsolete]")]
    [InlineData("[Obsolete(\"Use B.\")]")]
    [InlineData("[Obsolete(\"Use B in the next major version.\")]")]
    [InlineData("[System.Obsolete(\"Use B. Removal planned for barakoCMS 5.0.\")]")]
    [InlineData("[Serializable, Obsolete(\"Use B. Removal planned for barakoCMS 5.0.\")]")]
    [InlineData("[ObsoleteAttribute(\"Use B. Removal planned for barakoCMS 5.0.\")]")]
    [InlineData("[Obsolete(\"Use Apply(x)] instead. Removal planned for barakoCMS 5.0.\")]")]
    [InlineData("[Obsolete(\"Deprecated in barakoCMS 6.0, removal planned for barakoCMS 5.0.\")]")]
    public void A_deadline_this_release_reached_is_caught_however_the_attribute_is_written(string source)
    {
        Offenders(source, currentMajor: 5).Should().ContainSingle(source);
    }

    [Theory]
    [InlineData("[Obsolete(\"Use B. Removal planned for barakoCMS 6.0.\")]")]
    [InlineData("[Obsolete(\"Use B. \"\n        + \"B is removed in barakoCMS 6.0.\")]")]
    [InlineData("[Obsolete(\"Deprecated in barakoCMS 4.0, removal planned for barakoCMS 6.0.\", true)]")]
    [InlineData("// [Obsolete] is how a member is retired; see CLAUDE.md")]
    [InlineData("var obsolete = true; // nothing to do with the attribute")]
    public void A_deadline_a_major_away_is_not_flagged(string source)
    {
        Offenders(source, currentMajor: 5).Should().BeEmpty(source);
    }

    /// <summary>The problems with every <c>[Obsolete]</c> in <paramref name="source"/>.</summary>
    private static IEnumerable<string> Offenders(string source, int currentMajor)
    {
        foreach (Match match in ObsoleteAttribute.Matches(source))
        {
            if (IsInLineComment(source, match.Index))
            {
                continue;
            }

            var message = Arguments(source, match.Index + match.Length);

            // "the next major version" reads as a deadline and is not one: it means something
            // different in every release, so it can never come due.
            if (message.Contains("next major", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"relative deadline, name a version instead. {Trim(message)}";
                continue;
            }

            // A message can name more than one version ("deprecated in 4.0, removed in 6.0"). The
            // deadline is the one named after the word for removal, and any named one without it.
            var removal = Removal.Match(message);
            var named = NamedVersion.Matches(message)
                .Where(v => !removal.Success || v.Index > removal.Index)
                .DefaultIfEmpty(NamedVersion.Match(message))
                .Where(v => v.Success)
                .ToList();
            if (named.Count == 0)
            {
                yield return $"no removal version. {Trim(message)}";
                continue;
            }

            var deadline = named.MinBy(v => int.Parse(v.Groups[1].Value))!;
            if (int.Parse(deadline.Groups[1].Value) <= currentMajor)
            {
                yield return $"due in {deadline.Value} while building {currentMajor}.x, so remove it or move the deadline out";
            }
        }
    }

    /// <summary>
    /// The attribute's argument text, from the parenthesis after its name to the one that closes it,
    /// skipping parentheses inside string literals. Empty for a bare <c>[Obsolete]</c>.
    /// </summary>
    private static string Arguments(string source, int afterName)
    {
        var at = afterName;
        while (at < source.Length && char.IsWhiteSpace(source[at]))
        {
            at++;
        }

        if (at >= source.Length || source[at] != '(')
        {
            return string.Empty;
        }

        var depth = 0;
        var inString = false;
        for (var i = at; i < source.Length; i++)
        {
            var c = source[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return source[(at + 1)..i];
            }
        }

        return source[(at + 1)..];
    }

    private static bool IsInLineComment(string source, int index)
    {
        var lineStart = source.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        return source[lineStart..index].Contains("//", StringComparison.Ordinal);
    }

    private static string Trim(string message) =>
        message.Length <= 80 ? message : message[..80] + "...";

    private static IEnumerable<string> SourceFiles()
    {
        var root = RepositoryRoot();
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (!name.Equals("barakoCMS", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("BarakoCMS.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Equals("BarakoCMS.Tests", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    /// <summary>
    /// Walks up from the test binary to the directory holding the solution, so this works from a
    /// local run and from CI without either knowing where the other put things.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "barakoCMS.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test has to find the solution root to scan the source");
        return directory!.FullName;
    }
}
