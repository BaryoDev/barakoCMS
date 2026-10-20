using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Portability;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// The Portability routes and their bundle types are obsolete, and they are how these tests seed a tenant.
#pragma warning disable CS0618

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// GET /api/tenants/{handle}/export and POST /api/tenants/import: one tenant out as a bundle, and a
/// new tenant made from it (#897).
/// </summary>
[Collection("Sequential")]
public class TenantBundleTests
{
    private readonly IntegrationTestFixture _fixture;

    public TenantBundleTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> PlatformAdminAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private static string NewHandle() => $"tb-{Guid.NewGuid():N}"[..16];

    /// <summary>A tenant holding one content type and the given entries, made through the import route.</summary>
    private async Task<(string Slug, string Type, HttpClient Admin)> SeedAsync(IEnumerable<string> titles)
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        var type = $"bt{Guid.NewGuid():n}"[..12];

        var seeded = await admin.PostAsJsonAsync("/api/portability/import", new
        {
            contentTypes = new[]
            {
                new
                {
                    name = type, displayName = "Bundle", isPubliclyDeliverable = true,
                    fields = new[] { new { name = "Title", displayName = "Title", type = "string" } },
                },
            },
            contents = titles.Select(t => new
            {
                contentType = type, status = "Published", data = new Dictionary<string, object> { ["Title"] = t },
            }).ToArray(),
        }, Ct);
        seeded.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", seeded.StatusCode, await seeded.Content.ReadAsStringAsync(Ct));
        return (slug, type, admin);
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync(Ct);

    private static List<string> Titles(JsonElement bundle) =>
        bundle.GetProperty("contents").EnumerateArray()
            .Select(e => e.GetProperty("data").GetProperty("Title").GetString()!).ToList();

    private async Task<List<string>> TitlesInAsync(HttpClient admin, string type)
    {
        var exported = await admin.GetAsync($"/api/portability/export?types={type}", Ct);
        exported.StatusCode.Should().Be(HttpStatusCode.OK);
        var bundle = await exported.Content.ReadFromJsonAsync<PortabilityBundle>(ApiJson.Options, Ct);
        return bundle!.Contents.Select(c => c.Data["Title"].ToString()!).ToList();
    }

    [Fact]
    public async Task An_exported_tenant_imports_as_a_new_tenant_that_serves_the_same_content_and_the_source_is_untouched()
    {
        var other = await SeedAsync(["belongs to another tenant"]);
        var (slug, type, sourceAdmin) = await SeedAsync(["Kumquat one", "Kumquat two"]);
        var platform = await PlatformAdminAsync();

        var exported = await platform.GetAsync($"/api/tenants/{slug}/export", Ct);
        exported.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(exported));
        using var bundle = JsonDocument.Parse(await BodyAsync(exported));
        var root = bundle.RootElement;

        root.GetProperty("kind").GetString().Should().Be("barako.tenant-bundle");
        root.GetProperty("complete").GetBoolean().Should().BeTrue();
        root.GetProperty("config").GetProperty("contentTypes").TryGetProperty(type, out _).Should().BeTrue("the tenant's type is in the bundle");
        var titles = Titles(root);
        titles.Should().HaveCount(2);
        titles.Should().BeEquivalentTo("Kumquat one", "Kumquat two");
        (await BodyAsync(exported)).Should().NotContain("belongs to another tenant", "another tenant's entries are not this tenant's");

        var handle = NewHandle();
        var imported = await platform.PostAsJsonAsync("/api/tenants/import",
            new { handle, name = "Imported", bundle = root.Clone() }, Ct);
        imported.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(imported));

        var stored = await TenantProfileSeed.StoredAsync(_fixture, handle, Ct);
        stored.Name.Should().Be("Imported");
        stored.Status.Should().Be(TenantStatus.Active);

        var destinationAdmin = await TenantProfileSeed.AdminInAsync(_fixture, handle, Ct);
        var arrived = await TitlesInAsync(destinationAdmin, type);
        arrived.Should().HaveCount(2);
        arrived.Should().BeEquivalentTo("Kumquat one", "Kumquat two");

        (await TitlesInAsync(sourceAdmin, type)).Should().HaveCount(2, "the source tenant is read, never changed");
        (await TitlesInAsync(other.Admin, other.Type)).Should().ContainSingle("another tenant is untouched");
    }

    [Fact]
    public async Task A_tenant_larger_than_one_page_is_exported_whole_with_no_entry_repeated()
    {
        var titles = Enumerable.Range(0, TenantBundlePageSize + 50).Select(i => $"Entry {i:D4}").ToList();
        var (slug, _, _) = await SeedAsync(titles);
        var platform = await PlatformAdminAsync();

        var exported = await platform.GetAsync($"/api/tenants/{slug}/export", Ct);
        exported.StatusCode.Should().Be(HttpStatusCode.OK);
        using var bundle = JsonDocument.Parse(await BodyAsync(exported));

        var ids = bundle.RootElement.GetProperty("contents").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        ids.Should().HaveCount(titles.Count);
        ids.Should().OnlyHaveUniqueItems();
        Titles(bundle.RootElement).Should().BeEquivalentTo(titles);
    }

    // The export reads this many entries per round trip.
    private const int TenantBundlePageSize = 200;

    [Fact]
    public async Task A_bundle_holds_no_password_hash_api_key_or_mail_credential()
    {
        var (slug, _, _) = await SeedAsync(["Plain"]);
        const string passwordHash = "PASSWORD-HASH-MARKER-7f3a";
        const string keyHash = "API-KEY-HASH-MARKER-91bc";
        const string mailKey = "PROTECTED-MAIL-KEY-MARKER-55d0";

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var userId = Guid.NewGuid();
            session.Store(new User
            {
                Id = userId, Username = $"sec-{userId:n}"[..14], Email = $"sec-{userId:n}@example.com",
                PasswordHash = passwordHash, RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = slug, Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new ApiKey { Name = "marker key", KeyHash = keyHash, Prefix = "bk_marker", UserId = userId, TenantSlug = slug });
            session.Store(new EmailSettings
            {
                Id = EmailSettings.IdForTenant(slug), TenantSlug = slug, ProtectedApiKey = mailKey, FromAddress = "from@example.com",
            });
            await session.SaveChangesAsync(Ct);
        }

        var platform = await PlatformAdminAsync();
        var exported = await platform.GetAsync($"/api/tenants/{slug}/export", Ct);
        exported.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await BodyAsync(exported);

        text.Should().NotContain(passwordHash);
        text.Should().NotContain(keyHash);
        text.Should().NotContain(mailKey);
        text.Should().NotContain("marker key");
    }

    [Fact]
    public async Task Export_and_import_are_refused_to_anonymous_callers_and_to_a_role_without_the_capability()
    {
        var (slug, _, _) = await SeedAsync(["Guarded"]);
        var body = new { handle = NewHandle(), bundle = new { } };

        var anonymous = _fixture.CreateClient();
        (await anonymous.GetAsync($"/api/tenants/{slug}/export", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/tenants/import", body, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var editor = _fixture.CreateClient();
        editor.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("Admin"));
        (await editor.GetAsync($"/api/tenants/{slug}/export", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.PostAsJsonAsync("/api/tenants/import", body, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_role_holding_manage_tenants_reaches_the_export_and_gets_only_what_it_may_read()
    {
        var (slug, _, _) = await SeedAsync(["Held back"]);

        var roleName = $"Tenant Operator {Guid.NewGuid():N}";
        var userId = Guid.NewGuid();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var role = new Role { Id = Guid.NewGuid(), Name = roleName, SystemCapabilities = [SystemCapabilities.ManageTenants] };
            session.Store(role);
            session.Store(new User
            {
                Id = userId, Username = $"op-{userId:n}"[..14], Email = $"op-{userId:n}@example.com", RoleIds = [role.Id],
            });
            await session.SaveChangesAsync(Ct);
        }

        var operatorClient = _fixture.CreateClient();
        operatorClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _fixture.CreateToken(roles: [roleName], userId: userId.ToString()));

        var exported = await operatorClient.GetAsync($"/api/tenants/{slug}/export", Ct);
        exported.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(exported));
        using var bundle = JsonDocument.Parse(await BodyAsync(exported));

        bundle.RootElement.GetProperty("complete").GetBoolean().Should().BeFalse("the caller may read neither the sections nor the entries");
        bundle.RootElement.GetProperty("config").GetProperty("withheld").GetArrayLength().Should().BeGreaterThan(0);
        Titles(bundle.RootElement).Should().NotContain("Held back");
    }

    [Fact]
    public async Task Exporting_a_tenant_that_does_not_exist_answers_404()
    {
        var platform = await PlatformAdminAsync();
        (await platform.GetAsync($"/api/tenants/{NewHandle()}/export", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_import_never_writes_into_a_tenant_that_exists()
    {
        var (slug, type, admin) = await SeedAsync(["Existing"]);
        var platform = await PlatformAdminAsync();
        using var bundle = JsonDocument.Parse(await BodyAsync(await platform.GetAsync($"/api/tenants/{slug}/export", Ct)));

        var again = await platform.PostAsJsonAsync("/api/tenants/import", new { handle = slug, bundle = bundle.RootElement.Clone() }, Ct);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitlesInAsync(admin, type)).Should().ContainSingle("nothing was merged into the tenant");
    }

    [Fact]
    public async Task A_bundle_that_cannot_be_read_creates_no_tenant()
    {
        var platform = await PlatformAdminAsync();
        var handle = NewHandle();

        var notABundle = await platform.PostAsJsonAsync("/api/tenants/import", new { handle, bundle = new { kind = "something-else" } }, Ct);
        notABundle.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var newer = await platform.PostAsJsonAsync("/api/tenants/import", new
        {
            handle, bundle = new { kind = "barako.tenant-bundle", bundleVersion = 99, config = new { }, contents = Array.Empty<object>() },
        }, Ct);
        newer.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var unknownSection = await platform.PostAsJsonAsync("/api/tenants/import", new
        {
            handle, bundle = new
            {
                kind = "barako.tenant-bundle", bundleVersion = 1,
                config = new { version = 1, notASection = new { } }, contents = Array.Empty<object>(),
            },
        }, Ct);
        unknownSection.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the engine refuses a section it does not know");

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<Tenant>().AnyAsync(t => t.Slug == handle, Ct)).Should().BeFalse(
            "a refused import leaves no tenant behind, whether it failed to read or failed to apply");
    }

    [Fact]
    public async Task A_bundle_with_more_entries_than_the_limit_is_refused_before_anything_is_written()
    {
        var platform = await PlatformAdminAsync();
        var handle = NewHandle();
        var entries = Enumerable.Range(0, 5001).Select(i => new { id = Guid.NewGuid(), contentType = "x", data = new { Title = $"t{i}" } }).ToArray();

        var response = await platform.PostAsJsonAsync("/api/tenants/import", new
        {
            handle, bundle = new { kind = "barako.tenant-bundle", bundleVersion = 1, config = new { version = 1 }, contents = entries },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).Should().Contain("Tenants:MaxImportEntries");
        using var scope = _fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<Tenant>().AnyAsync(t => t.Slug == handle, Ct)).Should().BeFalse();
    }
}
