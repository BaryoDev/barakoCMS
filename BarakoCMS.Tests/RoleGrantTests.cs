using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Every role named in a <c>Roles(...)</c> gate is a role something actually creates.
/// </summary>
/// <remarks>
/// <c>/api/content-types</c> granted access to "Editor", which nothing has ever seeded. It granted
/// nothing, because a token only carries roles its user holds, so there was no live exposure. Two
/// reasons it was worth fixing anyway, both from the issue:
///
/// It misdescribes the permission model. Someone working out who can read schemas reads that line
/// and concludes Editors can, then carries that belief into the next endpoint they write.
///
/// And a dead grant can wake up. The day somebody adds a role named Editor for an unrelated reason,
/// it silently acquires schema read access nobody decided to give it, and there is no pull request
/// to point at because that one merged long ago.
///
/// The second instance is why this test exists rather than just the fix. By the time the issue was
/// picked up the same grant had appeared on the Files upload endpoint too, copied from an existing
/// line the way these things spread. A fix without a check leaves the third one to land the same way.
/// </remarks>
public class RoleGrantTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must be able to find the repository root");
        return dir!.FullName;
    }

    /// <summary>Production and module code. Not tests.</summary>
    /// <remarks>
    /// Test fixtures construct roles to set up their own scenarios, so scanning them would let a
    /// role that only a fixture creates count as seeded. The check would then pass while the
    /// application seeds nothing, which is the failure it exists to catch: a gate that is satisfied
    /// by its own test data proves nothing.
    /// </remarks>
    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            // Relative to the root, not the absolute path. A checkout that itself lives under a
            // worktree directory has that segment in every absolute path, and excluding on it throws
            // away the whole repository. The control below catches that, but only after the scan has
            // already silently become an empty one.
            .Select(f => (Full: f, Relative: Path.GetRelativePath(root, f)))
            .Where(x => !x.Relative.Contains($"obj{Path.DirectorySeparatorChar}")
                     && !x.Relative.Contains($"bin{Path.DirectorySeparatorChar}")
                     && !x.Relative.Contains($"BarakoCMS.Tests{Path.DirectorySeparatorChar}")
                     && !x.Relative.Contains($".claude{Path.DirectorySeparatorChar}"))
            .Select(x => x.Full);

    /// <summary>Roles handed to a FastEndpoints <c>Roles(...)</c> gate.</summary>
    private static readonly Regex Granted = new(@"\bRoles\(([^)]*)\)", RegexOptions.Compiled);

    /// <summary>Roles something constructs, wherever it lives.</summary>
    /// <remarks>
    /// Deliberately not just <c>DataSeeder</c>. "Accountant" is granted by the Accounting module and
    /// seeded by the Accounting module, which is correct and would look like a violation to a check
    /// that only knew about the core seeder. A module that grants a role it also creates is exactly
    /// the pattern this should permit.
    /// </remarks>
    private static readonly Regex Seeded = new(@"Name\s*=\s*""([A-Za-z][A-Za-z0-9_]*)""", RegexOptions.Compiled);

    private static readonly Regex Quoted = new(@"""([A-Za-z][A-Za-z0-9_]*)""", RegexOptions.Compiled);

    [Fact]
    public void No_endpoint_grants_a_role_that_nothing_creates()
    {
        var root = RepoRoot();

        var seeded = new HashSet<string>(StringComparer.Ordinal);
        var granted = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in SourceFiles(root))
        {
            var text = File.ReadAllText(file);

            // A Role is only ever created next to the word Role, which keeps content type names and
            // other Name = "..." assignments out of the seeded set.
            foreach (Match m in Seeded.Matches(text))
            {
                var line = text.LastIndexOf('\n', Math.Max(0, m.Index - 1));
                var context = text[Math.Max(0, line - 200)..Math.Min(text.Length, m.Index + 60)];
                if (context.Contains("Role", StringComparison.Ordinal))
                    seeded.Add(m.Groups[1].Value);
            }

            foreach (Match m in Granted.Matches(text))
            {
                foreach (Match role in Quoted.Matches(m.Groups[1].Value))
                    granted.TryAdd(role.Groups[1].Value, Path.GetRelativePath(root, file));
            }
        }

        // The control. A scan that matched nothing would report no violations, which is the shape of
        // gate this project keeps being bitten by.
        granted.Should().HaveCountGreaterThan(1,
            "only {0} granted roles were found, so an empty violation list proves nothing", granted.Count);
        seeded.Should().Contain("SuperAdmin", "the scan has to be finding seeded roles too");

        var dead = granted.Where(g => !seeded.Contains(g.Key))
            .Select(g => $"{g.Value} grants '{g.Key}', which nothing creates")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        dead.Should().BeEmpty(
            "a gate naming a role nobody creates grants nothing today and grants it silently the day "
          + "somebody adds that name for an unrelated reason");
    }
}

