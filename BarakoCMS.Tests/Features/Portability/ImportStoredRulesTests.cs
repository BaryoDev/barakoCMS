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
/// A type saved before rules were checked may store a rule a save refuses today. An import does not
/// refuse a bundle over that rule when the target already stores it, and still checks every field
/// the bundle adds or changes.
/// </summary>
[Collection("Sequential")]
public class ImportStoredRulesTests
{
    private const string UnknownRule = "matches";

    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;

    public ImportStoredRulesTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> TenantAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"rule-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
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
                Username = $"rule-{Guid.NewGuid():n}"[..14],
                Email = $"rule-{Guid.NewGuid():n}@example.com",
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
            TestRemoteIpFilter.Header, $"198.51.101.{Interlocked.Increment(ref _ipCounter) % 200 + 20}");
        return client;
    }

    /// <summary>
    /// Stored straight into the tenant, because no endpoint accepts this rule any more and the point
    /// is a type that was saved when one did.
    /// </summary>
    private async Task<string> StoredTypeWithAnUnknownRuleAsync(string tenantSlug)
    {
        var type = $"legacy{Guid.NewGuid():n}"[..14];

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantSlug);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Legacy",
            Fields =
            [
                new FieldDefinition
                {
                    Name = "Code",
                    DisplayName = "Code",
                    Type = "string",
                    ValidationRules = new() { [UnknownRule] = "^[A-Z]+$", ["maxLength"] = 12 },
                },
            ],
        });
        await session.SaveChangesAsync(Ct);

        return type;
    }

    private static async Task<PortabilityBundle> ExportAsync(HttpClient client, string type)
    {
        var exported = await client.GetAsync($"/api/portability/export?types={type}", Ct);
        exported.StatusCode.Should().Be(HttpStatusCode.OK);

        var bundle = await exported.Content.ReadFromJsonAsync<PortabilityBundle>(ApiJson.Options, Ct);

        // The rule has to be in the bundle, or the import below proves nothing about it.
        bundle!.ContentTypes.Should().HaveCount(1);
        bundle.ContentTypes[0].Fields.Should().HaveCount(1);
        bundle.ContentTypes[0].Fields[0].ValidationRules.Should().HaveCount(2);
        bundle.ContentTypes[0].Fields[0].ValidationRules.Should().ContainKey(UnknownRule);

        return bundle;
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, PortabilityBundle bundle) =>
        client.PostAsJsonAsync(
            "/api/portability/import",
            new { contentTypes = bundle.ContentTypes, contents = bundle.Contents },
            ApiJson.Options,
            Ct);

    [Fact]
    public async Task A_type_storing_an_unknown_rule_exports_and_imports_back_into_its_own_tenant()
    {
        var tenant = await TenantAsync();
        var type = await StoredTypeWithAnUnknownRuleAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var bundle = await ExportAsync(admin, type);
        var imported = await ImportAsync(admin, bundle);

        imported.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", imported.StatusCode,
            await imported.Content.ReadAsStringAsync(Ct));

        var after = await ExportAsync(admin, type);
        after.ContentTypes[0].Fields[0].Name.Should().Be("Code");
    }

    [Fact]
    public async Task A_new_field_with_an_unknown_rule_is_refused_without_blaming_the_stored_one()
    {
        var tenant = await TenantAsync();
        var type = await StoredTypeWithAnUnknownRuleAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var bundle = await ExportAsync(admin, type);
        bundle.ContentTypes[0].Fields.Add(new FieldDefinition
        {
            Name = "Score",
            DisplayName = "Score",
            Type = "int",
            ValidationRules = new() { ["maximum"] = 100 },
        });

        var imported = await ImportAsync(admin, bundle);

        imported.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await imported.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("Score").And.Contain("maximum");
        body.Should().NotContain(UnknownRule, "the stored field is unchanged, so its rule is not this request's to answer for");
    }

    [Fact]
    public async Task A_stored_field_whose_rules_the_bundle_changes_is_checked()
    {
        var tenant = await TenantAsync();
        var type = await StoredTypeWithAnUnknownRuleAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var bundle = await ExportAsync(admin, type);
        bundle.ContentTypes[0].Fields[0].ValidationRules[UnknownRule] = "^[a-z]+$";

        var imported = await ImportAsync(admin, bundle);

        imported.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await imported.Content.ReadAsStringAsync(Ct)).Should().Contain("Code").And.Contain(UnknownRule);
    }

    [Fact]
    public async Task A_type_storing_an_unknown_rule_is_refused_by_a_tenant_that_does_not_have_it()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var type = await StoredTypeWithAnUnknownRuleAsync(source);

        var bundle = await ExportAsync(await AdminOfAsync(source), type);
        var imported = await ImportAsync(await AdminOfAsync(destination), bundle);

        imported.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await imported.Content.ReadAsStringAsync(Ct)).Should().Contain("Code").And.Contain(UnknownRule);
    }
}
