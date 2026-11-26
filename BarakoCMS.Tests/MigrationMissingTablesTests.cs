using barakoCMS.Infrastructure.Migrations;
using FluentAssertions;
using JasperFx;
using Marten;
using Weasel.Core.Migrations;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>db-migrate</c> creates the tables a release adds and leaves every existing table alone, so
/// <c>db-assert</c> passes before the API starts and still fails on real drift.
/// </summary>
/// <remarks>
/// Each case uses a database of its own and a store built here with <c>AutoCreate.CreateOnly</c>,
/// the policy production runs. <c>db-assert</c> is
/// <c>IDatabase.AssertDatabaseMatchesConfigurationAsync</c>, which is what JasperFx's command calls.
/// Revert to see the first two red: remove the <c>MissingSchemaObjects.CreateAndReportAsync</c> call in
/// <c>MigrationCommand.ApplyAsync</c> and the new table is never created.
/// </remarks>
[Collection("Integration1")]
public class MigrationMissingTablesTests
{
    private readonly IntegrationTestFixture _factory;

    public MigrationMissingTablesTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>Stands for a document type that already shipped.</summary>
    public sealed class ExistingDocument
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    /// <summary>Stands for a document type a module adds in a later release.</summary>
    public sealed class AddedDocument
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private const string ExistingTable = "public.mt_doc_missing_tables_existing";
    private const string AddedTable = "public.mt_doc_missing_tables_added";

    private static DocumentStore StoreFor(string connectionString, bool withAdded, bool withDriftIndex = false) =>
        DocumentStore.For(options =>
        {
            options.Connection(connectionString);
            options.AutoCreateSchemaObjects = AutoCreate.CreateOnly;
            var existing = options.Schema.For<ExistingDocument>().DocumentAlias("missing_tables_existing");
            if (withDriftIndex)
            {
                existing.Index(x => x.Label);
            }

            if (withAdded)
            {
                options.Schema.For<AddedDocument>().DocumentAlias("missing_tables_added");
            }
        });

    private static ShippedMigration Insert(string name) =>
        ShippedMigrations.Parse(ShippedMigrations.CoreOwner, $"4.6.0/{name}.sql", $"insert into public.runs (name) values ('{name}');");

    private static async Task<(int Exit, string Output)> RunAsync(
        MigrationScratchDatabase database, IDocumentStore store, params string[] args)
    {
        var output = new StringWriter();
        var exit = await MigrationCommand.RunAsync(
            database.Ledger(), [Insert("first")], args, output, TestContext.Current.CancellationToken, store);
        return (exit, output.ToString());
    }

    private static Task<bool> ExistsAsync(MigrationScratchDatabase database, string table) =>
        database.IsTrueAsync($"select to_regclass('{table}') is not null");

    /// <summary>An upgrade whose build adds a document type: the existing table is already there, the new one is not.</summary>
    [Fact]
    public async Task A_loaded_module_that_adds_a_document_type_gets_its_table_from_db_migrate_and_db_assert_passes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        using (var before = StoreFor(database.ConnectionString, withAdded: false))
        {
            await before.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        }

        using var after = StoreFor(database.ConnectionString, withAdded: true);
        (await ExistsAsync(database, AddedTable)).Should().BeFalse("the release adds it");
        var refused = async () => await after.Storage.Database.AssertDatabaseMatchesConfigurationAsync(ct);
        await refused.Should().ThrowAsync<DatabaseValidationException>("this is the db-assert failure of the report");

        var run = await RunAsync(database, after);

        run.Exit.Should().Be(0, run.Output);
        run.Output.Should().Contain($"created    {AddedTable}");
        (await ExistsAsync(database, AddedTable)).Should().BeTrue();
        await after.Storage.Database.AssertDatabaseMatchesConfigurationAsync(ct);

        var again = await RunAsync(database, after);
        again.Exit.Should().Be(0, again.Output);
        again.Output.Should().Contain("0 missing schema object(s) created.");
    }

    /// <summary>
    /// A table that exists with an index the build does not declare in the database. Creating the
    /// missing table must not alter this one, or db-assert would stop catching drift.
    /// </summary>
    [Fact]
    public async Task A_table_that_exists_with_drift_is_not_altered_and_db_assert_still_fails_on_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        using (var before = StoreFor(database.ConnectionString, withAdded: false))
        {
            await before.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        }

        using var after = StoreFor(database.ConnectionString, withAdded: true, withDriftIndex: true);
        var indexExists = "select exists (select 1 from pg_indexes where tablename = 'mt_doc_missing_tables_existing' and indexname like '%idx_label')";
        (await database.IsTrueAsync(indexExists)).Should().BeFalse("the index is the drift");

        var run = await RunAsync(database, after);

        run.Exit.Should().Be(0, run.Output);
        (await ExistsAsync(database, AddedTable)).Should().BeTrue("the missing table is still created");
        (await database.IsTrueAsync(indexExists)).Should().BeFalse("an existing table is never altered");
        run.Output.Should().NotContain($"created    {ExistingTable}");
        var assert = async () => await after.Storage.Database.AssertDatabaseMatchesConfigurationAsync(ct);
        (await assert.Should().ThrowAsync<DatabaseValidationException>())
            .Which.Message.Should().Contain("mt_doc_missing_tables_existing");
    }

    [Fact]
    public async Task Status_names_the_missing_table_as_pending_and_writes_nothing()
    {
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        using (var before = StoreFor(database.ConnectionString, withAdded: false))
        {
            await before.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

            // The old build migrated, so the ledger is current and only the missing table is left.
            var migrated = await RunAsync(database, before);
            migrated.Exit.Should().Be(0, migrated.Output);
        }

        var ledger = await database.LedgerAsync();
        var runs = await database.RunsAsync();
        using var after = StoreFor(database.ConnectionString, withAdded: true);

        var status = await RunAsync(database, after, "--status");

        status.Exit.Should().Be(1, "the ledger is current and the table is missing: " + status.Output);
        status.Output.Should().Contain($"pending    create {AddedTable}");
        status.Output.Should().NotContain(ExistingTable, "an existing table is not listed");
        (await ExistsAsync(database, AddedTable)).Should().BeFalse("--status only reads");
        (await database.LedgerAsync()).Should().Equal(ledger, "--status does not write the ledger");
        (await database.RunsAsync()).Should().Equal(runs);
    }

    [Fact]
    public async Task Forget_still_removes_the_row_when_a_store_is_given()
    {
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory);
        using var store = StoreFor(database.ConnectionString, withAdded: true);
        var shipped = Insert("first");
        await RunAsync(database, store);

        var forget = await RunAsync(database, store, "--forget", shipped.Key);

        forget.Exit.Should().Be(0, forget.Output);
        forget.Output.Should().Contain($"forgot     {shipped.Key}");
        (await database.LedgerAsync()).Should().BeEmpty();
    }
}
