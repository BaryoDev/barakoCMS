using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A membership is written in one file, the one whose writers stage the audit entry beside the write.
/// </summary>
/// <remarks>
/// Creating a tenant stored its creator's Admin membership itself, and no entry said so. The
/// writers in <c>Members</c> (<c>AddAsync</c>, <c>ChangeAsync</c>, <c>RemoveAsync</c>) stage the entry
/// on the session they write on, so a membership written anywhere else is a grant with no entry.
///
/// A source check, because the behaviour of a writer that does not exist yet cannot be asserted.
/// <c>BarakoCMS.Testing</c> is left out: it is the test host, and it seeds a membership for a
/// caller it mints, with nobody acting.
/// </remarks>
public class MembershipWriterTests
{
    private const string Home = "barakoCMS/Features/Tenants/Members/Endpoints.cs";

    private static readonly string[] Writes =
    [
        "new Membership",
        "Patch<Membership>",
        "Delete<Membership>",
        "DeleteWhere<Membership>",
        "HardDeleteWhere<Membership>",
    ];

    [Fact]
    public void No_source_file_but_the_member_endpoints_writes_a_membership()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        var scanned = 0;
        var homeSeen = false;

        foreach (var file in SourceFiles(root))
        {
            scanned++;
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var text = File.ReadAllText(file);
            var writes = Writes.Where(w => text.Contains(w, StringComparison.Ordinal)).ToList();

            if (string.Equals(relative, Home, StringComparison.Ordinal))
            {
                homeSeen = true;
                writes.Should().Contain("new Membership", "the scan has to see the writes it is looking for");
                continue;
            }

            if (writes.Count > 0)
                offenders.Add($"{relative}: {string.Join(", ", writes)}");
        }

        scanned.Should().BeGreaterThan(100, "an empty scan would pass having read nothing");
        homeSeen.Should().BeTrue($"{Home} is where memberships are written");
        offenders.Should().BeEmpty(
            "a membership is written through Members.AddAsync, ChangeAsync or RemoveAsync, which "
            + "stage the audit entry on the same session");
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (!name.Equals("barakoCMS", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("BarakoCMS.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Equals("BarakoCMS.Tests", StringComparison.OrdinalIgnoreCase)
                || name.Equals("BarakoCMS.Testing", StringComparison.OrdinalIgnoreCase))
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

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "barakoCMS.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("barakoCMS.sln was not found above the test output directory.");
    }
}
