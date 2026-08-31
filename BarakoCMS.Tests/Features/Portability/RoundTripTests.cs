using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BarakoCMS.Portability;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// Export, then import what came out, and see whether the site on the other side is the same one.
/// </summary>
/// <remarks>
/// Portability runs during a migration, which is the moment when losing content hurts most and when
/// nobody is watching closely enough to notice a field that quietly did not survive the trip. The
/// round trip is the only assertion that covers both halves at once: a test of export alone passes
/// on a bundle no importer can read, and a test of import alone passes on a bundle nothing produces.
///
/// The tenant test is the one that would be a security bug rather than a data bug. A bundle carries
/// no tenant identity by design, so nothing in the file says where it belongs and the only thing
/// deciding is the tenant of the request carrying it.
/// </remarks>
[Collection("Sequential")]
public class RoundTripTests
{
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;

    public RoundTripTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private async Task<string> TenantAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"port-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return slug;
    }

    /// <summary>An admin of one tenant, with the membership the token issuer insists on.</summary>
    private async Task<HttpClient> AdminOfAsync(string tenantSlug)
    {
        var userId = Guid.NewGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"port-{Guid.NewGuid():n}"[..14],
                Email = $"port-{Guid.NewGuid():n}@example.com",
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
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(
                roles: ["SuperAdmin", "Admin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 200 + 20}");
        return client;
    }

    private static object Bundle(string type, string title) => new
    {
        contentTypes = new[]
        {
            new
            {
                name = type,
                displayName = "Round Tripped",
                description = "a type that has to survive the trip",
                isPubliclyDeliverable = true,
                fields = new[]
                {
                    new { name = "Title", displayName = "Title", type = "string" },
                    new { name = "Body", displayName = "Body", type = "text" },
                },
            },
        },
        contents = new[]
        {
            new
            {
                contentType = type,
                status = "Published",
                data = new Dictionary<string, object> { ["Title"] = title, ["Body"] = "the body" },
            },
        },
    };

    /// <summary>
    /// What comes out of export goes back in, and the type and its content arrive intact.
    /// </summary>
    [Fact]
    public async Task An_exported_bundle_imports_into_a_clean_tenant_with_its_schema_and_content()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var type = $"trip{Guid.NewGuid():n}"[..12];
        var title = $"Kumquat {Guid.NewGuid():n}"[..16];

        var sourceAdmin = await AdminOfAsync(source);
        (await sourceAdmin.PostAsJsonAsync("/api/portability/import", Bundle(type, title),
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();

        var exported = await sourceAdmin.GetAsync(
            $"/api/portability/export?types={type}", TestContext.Current.CancellationToken);
        exported.StatusCode.Should().Be(HttpStatusCode.OK);
        var bundle = await exported.Content.ReadFromJsonAsync<PortabilityBundle>(
            ApiJson.Options, TestContext.Current.CancellationToken);

        bundle!.ContentTypes.Should().ContainSingle().Which.Name.Should().Be(type);
        bundle.Contents.Should().ContainSingle();

        var destinationAdmin = await AdminOfAsync(destination);
        var imported = await destinationAdmin.PostAsJsonAsync(
            "/api/portability/import",
            new { contentTypes = bundle.ContentTypes, contents = bundle.Contents },
            ApiJson.Options,
            TestContext.Current.CancellationToken);
        imported.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", imported.StatusCode,
            await imported.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var landed = await destinationAdmin.GetAsync(
            $"/api/portability/export?types={type}", TestContext.Current.CancellationToken);
        var arrived = await landed.Content.ReadFromJsonAsync<PortabilityBundle>(
            ApiJson.Options, TestContext.Current.CancellationToken);

        var definition = arrived!.ContentTypes.Should().ContainSingle().Subject;
        definition.Name.Should().Be(type);
        definition.DisplayName.Should().Be("Round Tripped");
        definition.IsPubliclyDeliverable.Should().BeTrue("the delivery flag is part of the schema");
        definition.Fields.Select(f => f.Name).Should().BeEquivalentTo(["Title", "Body"],
            "a field lost in transit is content that stops being editable on the other side");

        var record = arrived.Contents.Should().ContainSingle().Subject;
        record.ContentType.Should().Be(type);
        record.Data["Title"].ToString().Should().Be(title);
        record.Data["Body"].ToString().Should().Be("the body");
    }

    /// <summary>
    /// Importing the same bundle twice upserts by name rather than leaving two of everything.
    /// </summary>
    /// <remarks>
    /// Reimporting is the ordinary thing to do when the first run half worked, so duplicating the
    /// schema on the second pass would punish exactly the person already having a bad day.
    /// </remarks>
    [Fact]
    public async Task Importing_the_same_bundle_twice_does_not_duplicate_the_content_type()
    {
        var tenant = await TenantAsync();
        var admin = await AdminOfAsync(tenant);
        var type = $"twice{Guid.NewGuid():n}"[..12];

        var first = await admin.PostAsJsonAsync("/api/portability/import", Bundle(type, "First"),
            TestContext.Current.CancellationToken);
        first.IsSuccessStatusCode.Should().BeTrue();
        (await Report(first)).GetProperty("contentTypesCreated").GetInt32().Should().Be(1);

        var second = await admin.PostAsJsonAsync("/api/portability/import", Bundle(type, "Second"),
            TestContext.Current.CancellationToken);
        second.IsSuccessStatusCode.Should().BeTrue();

        var report = await Report(second);
        report.GetProperty("contentTypesCreated").GetInt32().Should().Be(0);
        report.GetProperty("contentTypesUpdated").GetInt32().Should().Be(1,
            "the documented behaviour is upsert by name");

        var exported = await (await admin.GetAsync(
                $"/api/portability/export?types={type}", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<PortabilityBundle>(ApiJson.Options, TestContext.Current.CancellationToken);
        exported!.ContentTypes.Should().ContainSingle("a second import updates the type, it does not add another");
    }

    /// <summary>
    /// The bundle names no tenant, so the request does. An import lands in the caller's tenant and
    /// is invisible from anyone else's.
    /// </summary>
    [Fact]
    public async Task An_import_lands_in_the_calling_tenant_and_nowhere_else()
    {
        var mine = await TenantAsync();
        var theirs = await TenantAsync();
        var type = $"scoped{Guid.NewGuid():n}"[..12];

        var mineAdmin = await AdminOfAsync(mine);
        (await mineAdmin.PostAsJsonAsync("/api/portability/import", Bundle(type, "Mine"),
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();

        var mineExport = await (await mineAdmin.GetAsync(
                $"/api/portability/export?types={type}", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<PortabilityBundle>(ApiJson.Options, TestContext.Current.CancellationToken);
        mineExport!.Contents.Should().ContainSingle("the control: it did arrive somewhere");

        var theirsAdmin = await AdminOfAsync(theirs);
        var theirsExport = await (await theirsAdmin.GetAsync(
                $"/api/portability/export?types={type}", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<PortabilityBundle>(ApiJson.Options, TestContext.Current.CancellationToken);

        theirsExport!.ContentTypes.Should().BeEmpty(
            "a bundle carries no tenant identity, so the only thing keeping it out of another club "
            + "is the tenant of the request that delivered it");
        theirsExport.Contents.Should().BeEmpty();
    }

    /// <summary>
    /// Export is admin-only, which matters more than it looks: the bundle is every content row the
    /// tenant holds, in one response.
    /// </summary>
    [Fact]
    public async Task Export_and_import_are_closed_to_a_caller_without_an_admin_role()
    {
        var tenant = await TenantAsync();
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(
                roles: ["User"],
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenant }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);

        (await client.GetAsync("/api/portability/export", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "an export is a copy of everything, handed over in one request");

        (await client.PostAsJsonAsync("/api/portability/import", Bundle("anything", "x"),
                TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A truncated file is refused outright rather than applied as far as it parsed.
    /// </summary>
    /// <remarks>
    /// A partial import is worse than a failed one. It leaves a state nobody designed, and no clear
    /// way back, on a site somebody is in the middle of migrating.
    /// </remarks>
    [Fact]
    public async Task A_truncated_bundle_is_rejected_and_writes_nothing()
    {
        var tenant = await TenantAsync();
        var admin = await AdminOfAsync(tenant);
        var type = $"cut{Guid.NewGuid():n}"[..12];

        var whole = JsonSerializer.Serialize(Bundle(type, "Never Arrives"));
        var truncated = whole[..(whole.Length / 2)];

        var response = await admin.PostAsync(
            "/api/portability/import",
            new StringContent(truncated, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeFalse(
            "half a file is not a bundle, and saying so is the only safe answer");

        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenant);
        (await session.Query<ContentTypeDefinition>()
                .AnyAsync(d => d.Name == type, TestContext.Current.CancellationToken))
            .Should().BeFalse("nothing may be applied from a bundle that was refused");
    }

    private static async Task<JsonElement> Report(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
}
