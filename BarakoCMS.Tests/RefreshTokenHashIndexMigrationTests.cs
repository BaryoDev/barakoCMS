using System.Text.RegularExpressions;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The hand-applied refresh token hash index has to be the index Marten would have created.
/// </summary>
/// <remarks>
/// Same reasoning as <see cref="StoredFilesIndexMigrationTests"/>: refresh_tokens exists on every
/// deployed instance, CreateOnly never adds an index to it, and a name or expression written from
/// memory makes every start-up schema assertion ask to drop and recreate it.
/// </remarks>
[Collection("Sequential")]
public class RefreshTokenHashIndexMigrationTests
{
    private readonly IntegrationTestFixture _factory;

    public RefreshTokenHashIndexMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task The_hand_applied_migration_matches_the_index_Marten_creates()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        await using var session = store.QuerySession();
        var live = await session.AdvancedSql.QueryAsync<string>(
            "select i.indexdef from pg_indexes i "
          + "where i.schemaname = 'public' and i.tablename = 'mt_doc_refresh_tokens' "
          + "and i.indexdef like '%TokenHash%'",
            TestContext.Current.CancellationToken);

        var actual = live.SingleOrDefault();
        actual.Should().NotBeNull(
            "core declares .Index(x => x.TokenHash), so a database built from the model has it");

        var sql = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "migrations", "4.5.0", "refresh-token-hash-index.sql"),
            TestContext.Current.CancellationToken);

        Regex.IsMatch(sql, @"CREATE\s+UNIQUE\s+INDEX", RegexOptions.IgnoreCase).Should().Be(
            actual!.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase), "uniqueness has to match too");

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
