using barakoCMS.Events;
using barakoCMS.Extensions;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using JasperFx;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// With <c>Tenancy:Mode</c> set to Multi, background work visits registered tenants and leaves the
/// default partition and any unregistered partition as they are, and a value that is not a mode
/// stops the host (#895).
/// </summary>
/// <remarks>
/// These pass the mode to the code under test as configuration, on the fixture's own store, so
/// each one can run the same pass with and without the setting over the same rows.
/// </remarks>
[Collection("Sequential")]
public class TenancyModeBackgroundTests
{
    private readonly IntegrationTestFixture _fixture;

    public TenancyModeBackgroundTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static IConfiguration Configuration(string? mode) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [TenancyOptions.ModeKey] = mode })
            .Build();

    private const string PartitionsWithContentSql = "select distinct tenant_id from public.mt_doc_contents";

    [Fact]
    public async Task In_Multi_the_partition_listing_holds_registered_tenants_and_nothing_else()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();

        var active = await MultiTenancyHost.RegisterTenantAsync(_fixture);
        var inactive = await MultiTenancyHost.RegisterTenantAsync(_fixture, active: false);
        var unregistered = MultiTenancyHost.UnregisteredSlug();

        var stored = new List<(string? Partition, Guid Id)>();
        try
        {
            foreach (var partition in new[] { null, active, inactive, unregistered })
            {
                var id = Guid.NewGuid();
                await using var session = Open(store, partition);
                session.Store(new Content { Id = id, ContentType = "tenancy-mode-probe", Data = new Dictionary<string, object>() });
                await session.SaveChangesAsync(ct);
                stored.Add((partition, id));
            }

            var withNoSetting = await TenantPartitions.ListAsync(store, Configuration(null), PartitionsWithContentSql, ct);
            withNoSetting.Should().Contain(
                new[] { StorageConstants.DefaultTenantId, active, inactive, unregistered },
                "with no setting every partition that holds a row is visited, so all four hold one");

            var inMulti = await TenantPartitions.ListAsync(store, Configuration("Multi"), PartitionsWithContentSql, ct);
            inMulti.Should().NotBeEmpty();
            inMulti.Should().Contain(new[] { active, inactive },
                "an inactive tenant is still registered, and work queued before it was switched off still runs");
            inMulti.Should().NotContain(new[] { StorageConstants.DefaultTenantId, Tenant.DefaultSlug, unregistered },
                "in Multi the default partition and a partition nobody registered are left as they are");

            // What Multi leaves behind is found with the query docs/multi-tenancy.md gives, run as written.
            var leftBehind = new List<string>();
            await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
            {
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(DocumentedUnreachablePartitionsSql(), conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    leftBehind.Add(reader.GetString(0));
                }
            }

            leftBehind.Should().Contain(new[] { StorageConstants.DefaultTenantId, unregistered });
            leftBehind.Should().NotContain(new[] { active, inactive }, "a registered tenant's rows are not left behind");
        }
        finally
        {
            foreach (var (partition, id) in stored)
            {
                await using var session = Open(store, partition);
                session.Delete<Content>(id);
                await session.SaveChangesAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>The one sql block in the multi-tenancy doc that reads the content table.</summary>
    private static string DocumentedUnreachablePartitionsSql()
    {
        var doc = File.ReadAllText(Path.Combine(ComposeDefaultsTests.RepoRoot(), "docs", "multi-tenancy.md"));

        var blocks = System.Text.RegularExpressions.Regex
            .Matches(doc, "```sql\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Where(sql => sql.Contains("mt_doc_contents"))
            .ToList();

        return blocks.Should().ContainSingle("the doc gives one query for this").Subject;
    }

    private static IDocumentSession Open(IDocumentStore store, string? partition) =>
        partition is null ? store.LightweightSession() : store.LightweightSession(partition);

    /// <summary>
    /// One item, due, in the default partition. The Multi sweep runs and leaves it a draft; the
    /// same sweep with no setting then publishes it, which shows it was due all along.
    /// </summary>
    [Fact]
    public async Task In_Multi_the_scheduled_sweep_leaves_the_default_partition_as_it_is()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        var author = Guid.NewGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var writer = scope.ServiceProvider.GetRequiredService<barakoCMS.Core.Interfaces.IContentWriter>();

            var content = await writer.CreateAsync(new ContentCreated(
                id, "scheduled-article", new Dictionary<string, object> { ["Title"] = "due" },
                ContentStatus.Draft, author, "due", SensitivityLevel.Public, DateTime.UtcNow), ct);

            await writer.AppendAsync(content, new ContentScheduled(
                id, DateTime.UtcNow.AddMinutes(-5), null, author, DateTime.UtcNow), ct);

            await session.SaveChangesAsync(ct);
        }

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var logger = NullLogger<ScheduledContentService>.Instance;

        (await new ScheduledContentService(store, logger, Configuration("Multi"))
            .TrySweepAllTenantsAsync(DateTime.UtcNow, ct)).Should().BeTrue("nothing else holds the sweep lock in this fixture");
        (await StatusAsync(store, id, ct)).Should().Be(ContentStatus.Draft,
            "in Multi the default partition belongs to no tenant, so the sweep does not visit it");

        (await new ScheduledContentService(store, logger, Configuration(null))
            .TrySweepAllTenantsAsync(DateTime.UtcNow, ct)).Should().BeTrue();
        (await StatusAsync(store, id, ct)).Should().Be(ContentStatus.Published,
            "with no setting the same item is published, so it was due and only the mode held it back");
    }

    private static async Task<ContentStatus> StatusAsync(IDocumentStore store, Guid id, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        var content = await session.LoadAsync<Content>(id, ct);
        content.Should().NotBeNull();
        return content!.Status;
    }

    private IDictionary<string, string?> HostSettings(string mode) => new Dictionary<string, string?>
    {
        ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
        ["DATABASE_URL"] = string.Empty,
        ["JWT:Key"] = IntegrationTestFixture.JwtKey,
        ["JWT:Issuer"] = "BarakoTest",
        ["JWT:Audience"] = "BarakoClient",
        ["Connectors:Key"] = "test-connectors-key-that-is-its-own-and-long-enough",
        ["Seed:DemoContent"] = "false",
        [TenancyOptions.ModeKey] = mode,
    };

    /// <summary>
    /// Through <c>AddBarakoCMS</c>, which is where a deployment's configuration is read before the
    /// host is built. The host is never built here, so nothing connects to the database.
    /// </summary>
    [Fact]
    public void A_tenancy_mode_that_is_not_a_mode_stops_the_host_before_it_is_built()
    {
        var mistyped = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        mistyped.Configuration.AddInMemoryCollection(HostSettings("Mutli"));

        var act = () => mistyped.Services.AddBarakoCMS(mistyped.Configuration, modules => modules.Discover = false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Tenancy:Mode is 'Mutli'*Valid values: Single, Multi.");

        var multi = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        multi.Configuration.AddInMemoryCollection(HostSettings("Multi"));

        var accepted = () => multi.Services.AddBarakoCMS(multi.Configuration, modules => modules.Discover = false);

        accepted.Should().NotThrow("the same registration with a real mode goes through, so the refusal above is about the value");
    }
}
