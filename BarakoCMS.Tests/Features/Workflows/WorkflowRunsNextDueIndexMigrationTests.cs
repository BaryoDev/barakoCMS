using System.Text.RegularExpressions;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The hand-applied index migration has to create the index Marten would have created.
/// </summary>
/// <remarks>
/// Same reason as StoredFilesIndexMigrationTests: production runs AutoCreate.CreateOnly, so the
/// NextDueAt index lands on a fresh database only and an upgraded one gets it from the SQL file. A
/// name or an expression that differs from Marten's makes every start-up schema assertion ask to
/// drop and recreate it.
/// </remarks>
[Collection("Sequential")]
public class WorkflowRunsNextDueIndexMigrationTests
{
    private readonly IntegrationTestFixture _factory;

    public WorkflowRunsNextDueIndexMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task The_hand_applied_migration_matches_the_index_Marten_creates()
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();

        await using var session = store.QuerySession();
        var live = await session.AdvancedSql.QueryAsync<string>(
            "select i.indexdef from pg_indexes i "
          + "where i.schemaname = 'public' and i.tablename = 'mt_doc_workflow_runs' "
          + "and i.indexdef like '%NextDueAt%'",
            TestContext.Current.CancellationToken);

        var actual = live.SingleOrDefault();
        actual.Should().NotBeNull(
            "core declares .Index(x => x.NextDueAt), so a database built from the model has it");

        var sql = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "migrations", "4.6.0", "workflow-runs-next-due-index.sql"),
            TestContext.Current.CancellationToken);

        NameIn(sql).Should().Be(NameIn(actual!),
            "the migration has to create the index under the name Marten generates");

        Expression(sql).Should().Be(Expression(actual!),
            "and over the same expression, or it is a different index wearing the right name");
    }

    private static string NameIn(string sql) =>
        Match(sql,
            @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:CONCURRENTLY\s+)?(?:IF\s+NOT\s+EXISTS\s+)?([A-Za-z0-9_]+)",
            "a CREATE INDEX naming an index");

    /// <summary>
    /// The indexed expression with what Postgres adds or drops when it prints one taken out:
    /// whitespace, parentheses, the schema on the function and the cast on the key literal.
    /// </summary>
    private static string Expression(string sql)
    {
        var expression = Match(sql, @"USING\s+btree\s*\((.*)\)\s*;?\s*$", "a btree expression");

        return Regex.Replace(expression, @"\s+|[()]|public\.|::text", string.Empty);
    }

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
