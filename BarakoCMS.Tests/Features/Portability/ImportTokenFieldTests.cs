using System.Net;
using System.Net.Http.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Services;
using BarakoCMS.Portability;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// A token field in a bundle: the definition travels, the value does not. An export leaves it out
/// for every caller, an import generates a new one whatever the bundle carries, and a bundle may
/// not turn a stored token into another type.
/// </summary>
[Collection("Sequential")]
public class ImportTokenFieldTests
{
    private const string Forged = "forged0token0forged0token0forged";

    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;

    public ImportTokenFieldTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> TenantAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"tok-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
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
                Username = $"tok-{Guid.NewGuid():n}"[..14],
                Email = $"tok-{Guid.NewGuid():n}@example.com",
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
            TestRemoteIpFilter.Header, $"198.51.103.{Interlocked.Increment(ref _ipCounter) % 200 + 20}");
        return client;
    }

    /// <summary>A ticket type with a token field and one entry, written through the content writer.</summary>
    private async Task<(string Type, string Token)> TicketWithEntryAsync(string tenantSlug)
    {
        var type = $"ticket{Guid.NewGuid():n}"[..14];

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantSlug);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Ticket",
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition
                {
                    Name = "ClaimToken", DisplayName = "Claim token", Type = "token", Sensitivity = SensitivityLevel.Hidden,
                },
            ],
        });
        await session.SaveChangesAsync(Ct);

        IContentWriter writer = new ContentWriter(session, new ContentSourcingPolicyService(session));
        var created = await writer.CreateAsync(
            new ContentCreated(
                Guid.NewGuid(), type, new Dictionary<string, object> { ["Name"] = "Ana" }, ContentStatus.Published,
                Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow),
            Ct);
        await session.SaveChangesAsync(Ct);

        created.Data.Should().ContainKey("ClaimToken");
        return (type, created.Data["ClaimToken"].ToString()!);
    }

    private async Task<List<Content>> EntriesAsync(string tenantSlug, string type)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantSlug);
        return (await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct)).ToList();
    }

    private async Task<FieldDefinition> StoredFieldAsync(string tenantSlug, string type)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantSlug);
        var def = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
        return def.Fields.Single(f => f.Name == "ClaimToken");
    }

    private static async Task<(PortabilityBundle Bundle, string Raw)> ExportAsync(HttpClient client, string type)
    {
        var exported = await client.GetAsync($"/api/portability/export?types={type}", Ct);
        var raw = await exported.Content.ReadAsStringAsync(Ct);
        exported.StatusCode.Should().Be(HttpStatusCode.OK, raw);

        var bundle = System.Text.Json.JsonSerializer.Deserialize<PortabilityBundle>(raw, ApiJson.Options)!;
        bundle.ContentTypes.Should().HaveCount(1);
        bundle.ContentTypes[0].Fields.Should().HaveCount(2);
        bundle.Contents.Should().HaveCount(1, "the SuperAdmin exporting may read the entry");
        return (bundle, raw);
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, PortabilityBundle bundle) =>
        client.PostAsJsonAsync(
            "/api/portability/import",
            new { contentTypes = bundle.ContentTypes, contents = bundle.Contents },
            ApiJson.Options,
            Ct);

    [Fact]
    public async Task An_export_leaves_the_token_out_even_for_a_caller_who_may_read_it()
    {
        var tenant = await TenantAsync();
        var (type, token) = await TicketWithEntryAsync(tenant);

        var (bundle, raw) = await ExportAsync(await AdminOfAsync(tenant), type);

        bundle.Contents[0].Data.Should().ContainKey("Name", "the entry is exported, so the absence below is the token rule");
        bundle.Contents[0].Data.Should().NotContainKey("ClaimToken");
        raw.Should().NotContain(token);

        var field = bundle.ContentTypes[0].Fields.Single(f => f.Name == "ClaimToken");
        field.Type.Should().Be("token", "the definition travels");
        field.Sensitivity.Should().Be(SensitivityLevel.Hidden);
    }

    [Fact]
    public async Task An_import_generates_a_new_token_whatever_the_bundle_carries()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var (type, token) = await TicketWithEntryAsync(source);

        var (bundle, _) = await ExportAsync(await AdminOfAsync(source), type);
        bundle.Contents[0].Data["ClaimToken"] = Forged;

        var imported = await ImportAsync(await AdminOfAsync(destination), bundle);
        imported.StatusCode.Should().Be(HttpStatusCode.OK, await imported.Content.ReadAsStringAsync(Ct));

        var entries = await EntriesAsync(destination, type);
        entries.Should().HaveCount(1);
        var landed = entries[0].Data["ClaimToken"].ToString()!;
        landed.Should().NotBe(Forged).And.NotBe(token);
        TokenFieldProbe.ShouldBeAToken(landed);
        (await StoredFieldAsync(destination, type)).Sensitivity.Should().Be(SensitivityLevel.Hidden);
    }

    [Fact]
    public async Task A_bundle_that_turns_a_stored_token_into_text_is_refused()
    {
        var tenant = await TenantAsync();
        var (type, _) = await TicketWithEntryAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var (bundle, _) = await ExportAsync(admin, type);
        bundle.ContentTypes[0].Fields.Single(f => f.Name == "ClaimToken").Type = "string";
        bundle.Contents.Clear();

        var imported = await ImportAsync(admin, bundle);

        imported.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await imported.Content.ReadAsStringAsync(Ct)).Should().Contain("ClaimToken").And.Contain("token type");
        (await StoredFieldAsync(tenant, type)).Type.Should().Be("token");
    }

    [Fact]
    public async Task A_bundle_token_field_with_no_sensitivity_is_stored_hidden()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var (type, _) = await TicketWithEntryAsync(source);

        var (bundle, _) = await ExportAsync(await AdminOfAsync(source), type);
        bundle.ContentTypes[0].Fields.Single(f => f.Name == "ClaimToken").Sensitivity = SensitivityLevel.Public;

        var imported = await ImportAsync(await AdminOfAsync(destination), bundle);

        imported.StatusCode.Should().Be(HttpStatusCode.OK, await imported.Content.ReadAsStringAsync(Ct));
        (await StoredFieldAsync(destination, type)).Sensitivity.Should().Be(SensitivityLevel.Hidden);
    }
}
