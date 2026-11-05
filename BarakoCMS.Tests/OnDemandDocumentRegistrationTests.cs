// Aliased: Microsoft.Extensions.DependencyInjection also ships a ServiceCollectionExtensions.
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using barakoCMS.Models;
using FluentAssertions;
using JasperFx;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Five document types were created on first use until 5.5.0, and two requests doing that at once
/// failed on 42710 or 42P07. They are registered now, so the startup apply creates them. A live site
/// runs CreateOnly, which refuses to change a table, so the registration has to match the tables
/// those sites already have exactly.
/// </summary>
[Collection("Integration1")]
public class OnDemandDocumentRegistrationTests
{
    private readonly IntegrationTestFixture _factory;

    public OnDemandDocumentRegistrationTests(IntegrationTestFixture factory) => _factory = factory;

    private static readonly (Type Type, string Table)[] Documents =
    [
        (typeof(BlockBinding), "mt_doc_blockbinding"),
        (typeof(BlockPreset), "mt_doc_blockpreset"),
        (typeof(BlockSchema), "mt_doc_blockschema"),
        (typeof(UserGroup), "mt_doc_usergroup"),
        (typeof(WorkflowDefinition), "mt_doc_workflowdefinition"),
    ];

    [Fact]
    public async Task A_new_database_gets_the_tables_from_the_startup_apply()
    {
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory, started: false);
        await using var provider = Build(database.ConnectionString);
        var store = provider.GetRequiredService<IDocumentStore>();

        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        foreach (var (_, table) in Documents)
            (await database.IsTrueAsync($"select to_regclass('public.{table}') is not null"))
                .Should().BeTrue($"{table} has to exist before the first request, or two requests race to create it");
    }

    [Fact]
    public async Task Tables_created_on_first_use_by_an_older_release_need_no_change()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await MigrationScratchDatabase.CreateAsync(_factory, started: false);

        // What an older release did: no registration, so the table came from the store's policies
        // the first time a request touched the type.
        using (var older = DocumentStore.For(opts =>
               {
                   opts.Connection(database.ConnectionString);
                   opts.UseSystemTextJsonForSerialization();
                   opts.Policies.AllDocumentsAreMultiTenanted();
                   opts.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
               }))
        {
            foreach (var (type, _) in Documents)
                await older.Storage.Database.EnsureStorageExistsAsync(type, ct);
        }

        foreach (var (_, table) in Documents)
            (await database.IsTrueAsync($"select to_regclass('public.{table}') is not null"))
                .Should().BeTrue($"{table} is the setup");

        await using var provider = Build(database.ConnectionString);
        var store = provider.GetRequiredService<IDocumentStore>();

        foreach (var (type, table) in Documents)
        {
            var feature = ((DocumentStore)store).Options.Storage.FindFeature(type);
            var migration = await store.Storage.Database.CreateMigrationAsync(feature, ct);
            migration.Difference.Should().Be(SchemaPatchDifference.None,
                $"a live site cannot change {table} under CreateOnly, so the registration must match it");
        }
    }

    private ServiceProvider Build(string connectionString)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString,
            ["JWT:Key"] = "test-super-secret-key-that-is-at-least-32-chars-long",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        Host.AddBarakoCMS(services, config, m => m.Discover = false);

        // The fixture sets Development for the process, which picks CreateOrUpdate. A live site runs
        // CreateOnly, and IConfigureMarten runs after AddBarakoCMS, so this wins.
        services.ConfigureMarten(opts => opts.AutoCreateSchemaObjects = AutoCreate.CreateOnly);
        return services.BuildServiceProvider();
    }
}
