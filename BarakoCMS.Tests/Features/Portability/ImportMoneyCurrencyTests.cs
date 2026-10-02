using System.Net;
using System.Net.Http.Json;
using BarakoCMS.Portability;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// A money field's currency travels in a bundle, and an import does not change the one a stored
/// field declares: that goes through the currency endpoint, which counts the entries affected.
/// </summary>
[Collection("Sequential")]
public class ImportMoneyCurrencyTests
{
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;

    public ImportMoneyCurrencyTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> TenantAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"cur-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<HttpClient> AdminOfAsync(string tenantSlug)
    {
        var userId = Guid.NewGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"cur-{Guid.NewGuid():n}"[..14],
                Email = $"cur-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenantSlug,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(
                roles: ["SuperAdmin", "Admin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"198.51.102.{Interlocked.Increment(ref _ipCounter) % 200 + 20}");
        return client;
    }

    private async Task<string> StoredTypeInDollarsAsync(string tenantSlug)
    {
        var type = $"invoice{Guid.NewGuid():n}"[..14];

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantSlug);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Invoice",
            Fields =
            [
                new FieldDefinition { Name = "Total", DisplayName = "Total", Type = "money", Currency = "USD" },
            ],
        });
        await session.SaveChangesAsync(Ct);

        return type;
    }

    private async Task<FieldDefinition> StoredTotalAsync(string tenantSlug, string type)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantSlug);
        var def = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
        return def.Fields.Single(f => f.Name == "Total");
    }

    private static async Task<PortabilityBundle> ExportAsync(HttpClient client, string type)
    {
        var exported = await client.GetAsync($"/api/portability/export?types={type}", Ct);
        exported.StatusCode.Should().Be(HttpStatusCode.OK);

        var bundle = await exported.Content.ReadFromJsonAsync<PortabilityBundle>(ApiJson.Options, Ct);

        // The currency has to be in the bundle, or the imports below prove nothing about it.
        bundle!.ContentTypes.Should().HaveCount(1);
        bundle.ContentTypes[0].Fields.Should().HaveCount(1);
        bundle.ContentTypes[0].Fields[0].Currency.Should().Be("USD");

        return bundle;
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, PortabilityBundle bundle) =>
        client.PostAsJsonAsync(
            "/api/portability/import",
            new { contentTypes = bundle.ContentTypes, contents = bundle.Contents },
            ApiJson.Options,
            Ct);

    [Fact]
    public async Task A_type_declaring_a_currency_exports_and_imports_back_with_it()
    {
        var tenant = await TenantAsync();
        var type = await StoredTypeInDollarsAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var bundle = await ExportAsync(admin, type);
        var imported = await ImportAsync(admin, bundle);

        imported.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", imported.StatusCode,
            await imported.Content.ReadAsStringAsync(Ct));
        (await StoredTotalAsync(tenant, type)).Currency.Should().Be("USD");
    }

    [Fact]
    public async Task A_bundle_that_changes_a_stored_fields_currency_is_refused()
    {
        var tenant = await TenantAsync();
        var type = await StoredTypeInDollarsAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var bundle = await ExportAsync(admin, type);
        bundle.ContentTypes[0].Fields[0].Currency = "EUR";

        var imported = await ImportAsync(admin, bundle);

        imported.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await imported.Content.ReadAsStringAsync(Ct)).Should()
            .Contain("Total").And.Contain($"/api/content-types/{type}/fields/Total/currency");
        (await StoredTotalAsync(tenant, type)).Currency.Should().Be("USD");
    }

    [Fact]
    public async Task A_bundle_that_leaves_out_a_stored_fields_currency_is_refused_and_does_not_switch_it_off()
    {
        var tenant = await TenantAsync();
        var type = await StoredTypeInDollarsAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var bundle = await ExportAsync(admin, type);
        bundle.ContentTypes[0].Fields[0].Currency = null;

        var imported = await ImportAsync(admin, bundle);

        imported.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredTotalAsync(tenant, type)).Currency.Should().Be("USD");
    }

    [Fact]
    public async Task A_tenant_without_the_type_takes_the_bundles_currency_and_holds_its_entries_to_it()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var type = await StoredTypeInDollarsAsync(source);

        var bundle = await ExportAsync(await AdminOfAsync(source), type);
        bundle.Contents.Add(new ContentRecord
        {
            ContentType = type,
            Data = new Dictionary<string, object> { ["Total"] = 10.005m },
        });

        var refused = await ImportAsync(await AdminOfAsync(destination), bundle);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("Total").And.Contain("USD").And.Contain("2 decimal places");

        bundle.Contents[^1].Data["Total"] = 10.01m;
        var imported = await ImportAsync(await AdminOfAsync(destination), bundle);

        imported.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", imported.StatusCode,
            await imported.Content.ReadAsStringAsync(Ct));
        (await StoredTotalAsync(destination, type)).Currency.Should().Be("USD");
    }
}
