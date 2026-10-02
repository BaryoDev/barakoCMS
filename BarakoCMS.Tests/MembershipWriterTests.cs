using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A membership is written in one file, the one whose writers stage the audit entry beside the write.
/// </summary>
/// <remarks>
/// Creating a tenant stored its creator's Admin membership itself, and no entry said so. The
/// writers in <c>Members</c> (<c>AddAsync</c>, <c>ChangeAsync</c>, <c>RemoveAsync</c>) stage the entry
/// on the session they write on, and the patch they share, <c>QueueWrite</c>, is private to that
/// class, so nothing outside it can queue a write with no entry.
///
/// A source check, because the behaviour of a writer that does not exist yet cannot be asserted.
/// What it reads as a write, outside the member endpoints file and outside comment lines:
/// <list type="bullet">
/// <item><c>new Membership</c> as a whole word, and <c>Membership x = new(</c>.</item>
/// <item>A session call typed on the document: <c>Patch&lt;Membership&gt;</c>, <c>Store&lt;Membership&gt;</c>,
/// <c>Insert</c>, <c>Update</c>, <c>Delete</c>, <c>DeleteWhere</c>, <c>HardDelete</c>, <c>HardDeleteWhere</c>.</item>
/// <item>A file that loads or queries memberships and also calls <c>.Store(</c>, <c>.Insert(</c> or
/// <c>.Update(</c> on anything. That is the shape of a membership read, changed and stored back,
/// which names the type nowhere near the write. It is wider than the rule, on purpose: a file
/// that reads memberships and stores something else has to say so here.</item>
/// </list>
/// What it does not catch: a membership handed to another file that stores it, and a write
/// through raw SQL. A line inside a block comment is read as code.
///
/// <c>BarakoCMS.Testing</c> is left out: it is the test host, and it seeds a membership for a
/// caller it mints, with nobody acting.
/// </remarks>
public class MembershipWriterTests
{
    private const string Home = "barakoCMS/Features/Tenants/Members/Endpoints.cs";

    private static readonly Regex Constructs = new(
        @"\bnew\s+Membership\b|\bMembership\s+\w+\s*=\s*new\s*\(", RegexOptions.Compiled);

    private static readonly Regex TypedWrite = new(
        @"\b(Patch|Store|Insert|Update|Delete|DeleteWhere|HardDelete|HardDeleteWhere)<(\w+\.)*Membership>",
        RegexOptions.Compiled);

    private static readonly Regex Reads = new(
        @"\b(Query|LoadAsync|LoadManyAsync)<(\w+\.)*Membership>", RegexOptions.Compiled);

    private static readonly Regex StoresSomething = new(@"\.(Store|Insert|Update)\(", RegexOptions.Compiled);

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
            var found = WritesIn(File.ReadAllLines(file));

            if (string.Equals(relative, Home, StringComparison.Ordinal))
            {
                homeSeen = true;
                found.Should().NotBeEmpty("the scan has to see the writes it is looking for");
                continue;
            }

            if (found.Count > 0)
                offenders.Add($"{relative}: {string.Join(", ", found)}");
        }

        scanned.Should().BeGreaterThan(100, "an empty scan would pass having read nothing");
        homeSeen.Should().BeTrue($"{Home} is where memberships are written");
        offenders.Should().BeEmpty(
            "a membership is written through Members.AddAsync, ChangeAsync or RemoveAsync, which "
            + "stage the audit entry on the same session");
    }

    [Theory]
    [InlineData("session.Store(new Membership { UserId = id });")]
    [InlineData("var m = new Membership();")]
    [InlineData("Membership m = new() { UserId = id };")]
    [InlineData("session.Patch<Membership>(id).Set(x => x.Status, status);")]
    [InlineData("session.Delete<Models.Membership>(id);")]
    public void A_line_that_writes_a_membership_is_seen(string line)
    {
        WritesIn([line]).Should().HaveCount(1);
    }

    [Theory]
    [InlineData("var response = new MembershipResponse(m.UserId);")]
    [InlineData("return new MembershipRoles();")]
    [InlineData("    // session.Patch<Membership>(id) is what the writers do")]
    [InlineData("    /// <c>new Membership</c> is built by AddAsync.")]
    [InlineData("var held = await session.Query<Membership>().ToListAsync(ct);")]
    [InlineData("session.Store(role);")]
    public void A_line_that_only_looks_like_one_is_not(string line)
    {
        WritesIn([line]).Should().BeEmpty();
    }

    [Fact]
    public void A_file_that_reads_memberships_and_stores_anything_is_seen()
    {
        string[] lines =
        [
            "var m = await session.Query<Membership>().FirstAsync(x => x.UserId == id, ct);",
            "m.Status = MembershipStatus.Suspended;",
            "session.Store(m);",
        ];

        var found = WritesIn(lines);

        found.Should().HaveCount(1);
        found[0].Should().Contain("reads memberships");
    }

    private static List<string> WritesIn(IReadOnlyList<string> lines)
    {
        var code = lines.Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)).ToList();
        var found = new List<string>();

        foreach (var line in code)
        {
            foreach (var match in Constructs.Matches(line).Concat(TypedWrite.Matches(line)))
                found.Add(match.Value);
        }

        if (code.Any(line => Reads.IsMatch(line)) && code.Any(line => StoresSomething.IsMatch(line)))
            found.Add("reads memberships and stores a document");

        return found;
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
