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
    /// A statement: the keywords, an optional IF NOT EXISTS, a name, then a semicolon, a closing quote
    /// (a psql -c string), or WITH, SCHEMA, VERSION or CASCADE. The name is bare, double quoted, or a
    /// placeholder that starts with <c>{</c>, <c>%</c>, <c>$</c> or <c>:</c> (an interpolated string, a
    /// format call or a psql variable). Whitespace between the keywords may include line breaks, so a
    /// statement split across lines counts. A bare end of line after the name does not count, so prose
    /// such as "drops the CREATE EXTENSION and" does not match with the name "and".
    /// </summary>
    internal static readonly Regex Statement = new(
        @"\bcreate\s+extension\s+(?:if\s+not\s+exists\s+)?(?:""[^""]+""|[{%$:][^\s;""']*|[A-Za-z_][\w$-]*)(?:[ \t]*;|[ \t]*[""']|\s+(?:with|schema|version|cascade)\b)",
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

            if (Allowed.Contains(path))
            {
                continue;
            }

            foreach (var line in StatementLines(Encoding.UTF8.GetString(bytes)))
            {
                found.Add($"{path}:{line}");
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
            StatementLines(File.ReadAllText(Path.Combine(RepoPaths.Root, path))).Should().NotBeEmpty("{0} holds the statement the scan looks for", path);
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
    [InlineData("var sql = $\"CREATE EXTENSION IF NOT EXISTS {name};\";", true)]
    [InlineData("format('CREATE EXTENSION IF NOT EXISTS %I;', x)", true)]
    [InlineData("CREATE EXTENSION IF NOT EXISTS :ext;", true)]
    [InlineData("CREATE EXTENSION\n  IF NOT EXISTS hstore;", true)]
    [InlineData("the stream drops the CREATE EXTENSION\nand COMMENT ON EXTENSION lines", false)]
    public void The_pattern_matches_statements_and_not_mentions(string text, bool matches)
    {
        StatementLines(text).Any().Should().Be(matches);
    }

    [Fact]
    public void A_match_reports_the_line_it_starts_on()
    {
        StatementLines("one\ntwo\nCREATE EXTENSION\n  IF NOT EXISTS hstore;\n").Should().Equal(3);
    }

    /// <summary>The 1-based line each statement starts on, scanning the whole text.</summary>
    private static IEnumerable<int> StatementLines(string text)
    {
        foreach (Match match in Statement.Matches(text))
        {
            var line = 1;
            for (var i = 0; i < match.Index; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }

            yield return line;
        }
    }

    // Any top-level test project, such as BarakoCMS.Tests/ or BarakoCMS.Pages.Tests/.
    private static bool IsExcluded(string path) =>
        path.Split('/')[0].EndsWith(".Tests", StringComparison.Ordinal)
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
