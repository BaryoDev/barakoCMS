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
    private static readonly Regex ObsoleteAttribute = new(@"\[Obsolete\((.*?)\)\]", RegexOptions.Singleline);
    private static readonly Regex NamedVersion = new(@"barakoCMS (\d+)\.0");

    [Fact]
    public void No_obsolete_member_names_a_deadline_this_release_has_already_reached()
    {
        var currentMajor = typeof(barakoCMS.Models.Content).Assembly.GetName().Version!.Major;
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in ObsoleteAttribute.Matches(text))
            {
                var message = match.Groups[1].Value;

                // "the next major version" reads as a deadline and is not one: it means something
                // different in every release, so it can never come due.
                if (message.Contains("next major", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{Path.GetFileName(file)}: relative deadline, name a version instead. {Trim(message)}");
                    continue;
                }

                var named = NamedVersion.Match(message);
                if (!named.Success)
                {
                    offenders.Add($"{Path.GetFileName(file)}: no removal version. {Trim(message)}");
                    continue;
                }

                if (int.Parse(named.Groups[1].Value) <= currentMajor)
                {
                    offenders.Add($"{Path.GetFileName(file)}: due in {named.Value} while building {currentMajor}.x, so remove it or move the deadline out");
                }
            }
        }

        offenders.Should().BeEmpty();
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
