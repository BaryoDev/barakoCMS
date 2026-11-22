using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The only files in the repository that hold a statement creating a Postgres extension are the owner script and its
/// quickstart copy, which run as the database owner. The API connects as a role that may not create
/// one, so a statement anywhere else would only ever fail there.
/// </summary>
/// <remarks>
/// The scan matches statements, not mentions: prose or a comment that names the statement is fine.
/// This proves the repository's own sources ask for no extension. It does not prove that nothing asks
/// at runtime: Marten and Weasel can emit such a statement from their own code. The runtime checks in
/// <c>scripts/compose-runtime-check.sh</c>, <c>scripts/compose-secrets-runtime-check.sh</c> and
/// <c>scripts/quickstart-runtime-check.sh</c> cover the libraries, by reading the API's log for a
/// refused extension on a fresh volume.
/// </remarks>
public class ExtensionStatementTests
{
    private static readonly string[] Allowed =
    [
        "scripts/db-app-role.sql",
        "quickstart/scripts/db-app-role.sql",
    ];

    /// <summary>
    /// A statement: the keywords, an optional IF NOT EXISTS, a name (bare or double quoted), then a
    /// semicolon, a closing quote (a psql -c string), or WITH, SCHEMA, VERSION or CASCADE. A bare end
    /// of line after the name does not count, so prose such as "drops the CREATE EXTENSION and" does not
    /// match with the name "and".
    /// </summary>
    internal static readonly Regex Statement = new(
        @"\bcreate\s+extension\s+(?:if\s+not\s+exists\s+)?(?:""[^""]+""|[A-Za-z_][\w$-]*)(?:\s*;|\s*[""']|\s+(?:with|schema|version|cascade)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const long LargestFile = 5 * 1024 * 1024;

    [Fact]
    public void Only_the_owner_script_and_its_copy_create_an_extension()
    {
        var found = new List<string>();
        foreach (var path in TrackedFiles())
        {
            if (IsExcluded(path))
            {
                continue;
            }

            var full = Path.Combine(RepoPaths.Root, path);
            if (!File.Exists(full) || new FileInfo(full).Length > LargestFile)
            {
                continue;
            }

            var bytes = File.ReadAllBytes(full);
            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                continue; // a binary file
            }

            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (Statement.IsMatch(lines[i]) && !Allowed.Contains(path))
                {
                    found.Add($"{path}:{i + 1}");
                }
            }
        }

        found.Should().BeEmpty(
            "only {0} may create an extension; the API's role is not allowed to, so a statement in "
            + "these places fails at runtime. Found at: {1}", string.Join(" and ", Allowed), string.Join(", ", found));
    }

    [Fact]
    public void The_owner_script_and_its_copy_are_where_the_scan_looks()
    {
        // A scan that finds no file proves nothing, so the allowed files have to be in it.
        var files = TrackedFiles().ToList();
        files.Should().Contain(Allowed);
        foreach (var path in Allowed)
        {
            File.ReadAllText(Path.Combine(RepoPaths.Root, path)).Split('\n').Any(l => Statement.IsMatch(l)).Should().BeTrue("{0} holds the statement the scan looks for", path);
        }
    }

    [Theory]
    [InlineData("4. Restore as the app role: the stream drops the `CREATE EXTENSION` and `COMMENT ON EXTENSION` lines", false)]
    [InlineData("# Restore as the app role. The awk drops CREATE EXTENSION and COMMENT ON EXTENSION lines and", false)]
    [InlineData("the extensions in the empty database first, the restore stream drops the `CREATE EXTENSION` and", false)]
    [InlineData("# the role and the extensions in the empty database, the stream drops the CREATE EXTENSION and", false)]
    [InlineData("# Drops CREATE EXTENSION and COMMENT ON EXTENSION lines, and leaves COPY data blocks alone.", false)]
    [InlineData("CREATE EXTENSION IF NOT EXISTS pg_stat_statements SCHEMA public;", true)]
    [InlineData("create extension hstore;", true)]
    [InlineData("psql -c \"CREATE EXTENSION IF NOT EXISTS unaccent\"", true)]
    [InlineData("CREATE EXTENSION \"uuid-ossp\" WITH SCHEMA public;", true)]
    [InlineData("CREATE EXTENSION vector CASCADE;", true)]
    public void The_pattern_matches_statements_and_not_mentions(string line, bool matches)
    {
        Statement.IsMatch(line).Should().Be(matches);
    }

    private static bool IsExcluded(string path) =>
        path.StartsWith("BarakoCMS.Tests/", StringComparison.Ordinal)
        || path == "CHANGELOG.md"
        || path.StartsWith("changelog.d/", StringComparison.Ordinal);

    /// <summary>Tracked files, as repository-relative paths with forward slashes.</summary>
    private static IEnumerable<string> TrackedFiles()
    {
        var listed = GitFiles();
        if (listed is not null)
        {
            return listed;
        }

        // No git here (a source archive): every file, minus what a build or a checkout leaves behind.
        var skip = new[] { "/.git/", "/bin/", "/obj/", "/node_modules/" };
        return Directory.EnumerateFiles(RepoPaths.Root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(RepoPaths.Root, f).Replace('\\', '/'))
            .Where(p => !skip.Any(s => ("/" + p).Contains(s, StringComparison.Ordinal)));
    }

    private static List<string>? GitFiles()
    {
        try
        {
            var start = new ProcessStartInfo("git", "ls-files -z")
            {
                WorkingDirectory = RepoPaths.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0
                ? output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
