using System.Text.RegularExpressions;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The hand-applied index migration has to create the index Marten would have created.
/// </summary>
/// <remarks>
/// `stored_files` exists on every deployed instance, and production runs `AutoCreate.CreateOnly`,
/// which creates a missing object and never alters one that is there. So the `ParentFileId` index
/// the module declares lands on a fresh database only, and an upgraded one needs
/// `migrations/4.2.0/stored-files-parent-index.sql` applied by hand.
///
/// Which means the two have to agree, and a name or an expression written from memory does not.
/// This reads the index Marten actually built on the fixture's database and checks the SQL file
/// against it. The same mismatch went unnoticed on the redirects index in #112 until the upgrade job
/// caught it after merge: same columns, same uniqueness, a name Marten does not generate, so every
/// start-up assertion wanted to drop and recreate it.
/// </remarks>
[Collection("Sequential")]
public class StoredFilesIndexMigrationTests
{
    private readonly IntegrationTestFixture _factory;

    public StoredFilesIndexMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task The_hand_applied_migration_matches_the_index_Marten_creates()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        await using var session = store.QuerySession();
        var live = await session.AdvancedSql.QueryAsync<string>(
            "select i.indexdef from pg_indexes i "
          + "where i.schemaname = 'public' and i.tablename = 'mt_doc_stored_files' "
          + "and i.indexdef like '%ParentFileId%'",
            TestContext.Current.CancellationToken);

        var actual = live.SingleOrDefault();
        actual.Should().NotBeNull(
            "the module declares .Index(x => x.ParentFileId), so a database built from the model has it");

        var sql = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "migrations", "4.2.0", "stored-files-parent-index.sql"),
            TestContext.Current.CancellationToken);

        NameIn(actual!).Should().Be(NameIn(sql),
            "the migration has to create the index under the name Marten generates, or a start-up "
          + "schema assertion asks to drop and recreate it on every boot");

        Expression(sql).Should().Be(Expression(actual!),
            "and over the same expression, or it is a different index wearing the right name");
    }

    // Anchored on CREATE ... INDEX, because the migration file's own comments say the word "index"
    // and an unanchored pattern happily matched one of those instead.
    private static string NameIn(string sql) =>
        Match(sql,
            @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:CONCURRENTLY\s+)?(?:IF\s+NOT\s+EXISTS\s+)?([A-Za-z0-9_]+)",
            "a CREATE INDEX naming an index");

    /// <summary>The indexed expression, whitespace collapsed so formatting is not a difference.</summary>
    private static string Expression(string sql) =>
        Regex.Replace(Match(sql, @"USING\s+btree\s*\((.*)\)\s*;?\s*$", "a btree expression"), @"\s+", " ")
            .Trim()
            .TrimEnd(')')
            .TrimStart('(')
            .Trim();

    private static string Match(string sql, string pattern, string what)
    {
        var match = Regex.Match(sql, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        match.Success.Should().BeTrue($"the statement should contain {what}: {sql}");
        return match.Groups[1].Value;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test binary should sit under the repository");
        return directory!.FullName;
    }
}
