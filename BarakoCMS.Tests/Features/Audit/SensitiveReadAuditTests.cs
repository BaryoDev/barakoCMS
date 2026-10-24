using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Site;

namespace BarakoCMS.Tests.Features.Audit;

/// <summary>
/// Reads of data above Public write one row per request naming the entries and fields, never a
/// value (#916), and a share link opened or redeemed writes a row naming the link.
/// </summary>
[Collection("Integration2")]
public class SensitiveReadAuditTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ShareLinkTestHost _host;

    public SensitiveReadAuditTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _host = new ShareLinkTestHost(fixture);
    }

    private static CancellationToken Ct => ShareLinkTestHost.Ct;

    private async Task<List<AuditEvent>> RowsAsync(string tenant, string action)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<AuditEvent>()
            .Where(e => e.TenantSlug == tenant && e.Action == action)
            .ToListAsync(Ct)).ToList();
    }

    private static async Task<JsonElement> OkJsonAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static List<JsonElement> Entries(AuditEvent row)
    {
        var entries = row.Element("entries");
        return entries.GetProperty("items").EnumerateArray().ToList();
    }

    [Fact]
    public async Task A_staff_read_of_a_Members_field_writes_no_row_while_a_Sensitive_one_still_does()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var entry = await _host.EntryAsync(tenant, type, "club");

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(tenant))
        {
            var def = await session.Query<ContentTypeDefinition>().FirstAsync(d => d.Name == type, Ct);
            def.Fields.Add(new FieldDefinition
            {
                Name = "Perk", DisplayName = "Perk", Type = "string", Sensitivity = SensitivityLevel.Members,
            });
            session.Store(def);
            var content = await session.LoadAsync<Content>(entry, Ct);
            content!.Data["Perk"] = "perk-value";
            session.Store(content);
            await session.SaveChangesAsync(Ct);
        }

        var admin = await _host.SuperAdminInAsync(tenant);
        var body = await OkJsonAsync(await admin.GetAsync($"/api/contents/{entry}", Ct));
        body.GetProperty("data").GetProperty("Perk").GetString().Should().Be("perk-value", "staff read it");

        var rows = await RowsAsync(tenant, "content.sensitive.read");
        rows.Should().HaveCount(1, "the Sensitive field is still recorded");
        rows[0].Strings("fields").Should().Equal("Secret");
    }

    [Fact]
    public async Task Reading_an_entry_whose_sensitive_field_the_caller_sees_writes_one_row_naming_the_field()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var entry = await _host.EntryAsync(tenant, type, "chart");
        var admin = await _host.SuperAdminInAsync(tenant);

        var body = await OkJsonAsync(await admin.GetAsync($"/api/contents/{entry}", Ct));
        body.GetProperty("data").GetProperty("Secret").GetString().Should().Be(ShareLinkTestHost.SecretValue);

        var rows = await RowsAsync(tenant, "content.sensitive.read");
        rows.Should().HaveCount(1);
        var row = rows[0];
        row.TargetType.Should().Be("content");
        row.TargetId.Should().Be(entry.ToString());
        row.ActorUserId.Should().NotBeNull();
        row.Strings("fields").Should().Equal("Secret");
        row.Text("contentType").Should().Be(type);
        row.Text("sensitivity").Should().Be("Public");
        row.Json().Should().NotContain(ShareLinkTestHost.SecretValue, "a row names fields, never their values");
    }

    [Fact]
    public async Task Reading_an_entry_with_the_sensitive_field_masked_writes_no_row()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var entry = await _host.EntryAsync(tenant, type, "chart");
        var reader = await _host.EditorInAsync(tenant, type, mayRead: true, mayUpdate: false);

        var body = await OkJsonAsync(await reader.GetAsync($"/api/contents/{entry}", Ct));
        body.GetProperty("data").GetProperty("Title").GetString().Should().Be("T-chart", "the read itself succeeded");
        body.GetProperty("data").GetProperty("Secret").GetString().Should().NotBe(ShareLinkTestHost.SecretValue);

        (await RowsAsync(tenant, "content.sensitive.read")).Should().BeEmpty("nothing above Public went out");

        // The control: the same entry read by a caller who sees the field is recorded, so the
        // query above was looking in the right place.
        var admin = await _host.SuperAdminInAsync(tenant);
        (await admin.GetAsync($"/api/contents/{entry}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowsAsync(tenant, "content.sensitive.read")).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_sensitive_entry_read_by_slug_records_the_documents_level()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var entry = await _host.EntryAsync(tenant, type, "file", sensitivity: SensitivityLevel.Sensitive);
        var admin = await _host.SuperAdminInAsync(tenant);

        (await admin.GetAsync($"/api/contents/by-slug/{type}/file", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await RowsAsync(tenant, "content.sensitive.read");
        rows.Should().HaveCount(1);
        rows[0].TargetId.Should().Be(entry.ToString());
        rows[0].Text("sensitivity").Should().Be("Sensitive");
        rows[0].Text("route").Should().Contain("{slug}").And.NotContain("/file", "a slug can be a Sensitive value");
    }

    [Fact]
    public async Task A_list_read_writes_one_row_for_the_page_naming_each_entry()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var first = await _host.EntryAsync(tenant, type, "one");
        var second = await _host.EntryAsync(tenant, type, "two");
        var admin = await _host.SuperAdminInAsync(tenant);

        var page = await OkJsonAsync(await admin.GetAsync($"/api/contents?contentType={type}&pageSize=10", Ct));
        page.GetProperty("items").GetArrayLength().Should().Be(2);

        var rows = await RowsAsync(tenant, "content.sensitive.read");
        rows.Should().HaveCount(1, "a list read is one row, however many entries it sends");
        rows[0].TargetId.Should().BeNull();
        rows[0].Element("entries").GetProperty("count").GetInt32().Should().Be(2);
        var entries = Entries(rows[0]);
        entries.Should().HaveCount(2);
        entries.Select(e => e.GetProperty("id").GetString()).Should().BeEquivalentTo(
            [first.ToString(), second.ToString()]);
        entries.Should().AllSatisfy(e => e.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetString()).Should().Equal("Secret"));
        rows[0].Json().Should().NotContain(ShareLinkTestHost.SecretValue);
    }

    /// <summary>An entry with two versions in its stream, created and updated through the API.</summary>
    private static async Task<Guid> VersionedEntryAsync(HttpClient admin, string type)
    {
        var created = await admin.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object>
            {
                ["Title"] = "first", ["Slug"] = "versioned", ["Secret"] = ShareLinkTestHost.SecretValue,
            },
        }, Ct);
        var id = (await OkOrCreatedJsonAsync(created)).GetProperty("id").GetGuid();

        var updated = await admin.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object>
            {
                ["Title"] = "second", ["Slug"] = "versioned", ["Secret"] = ShareLinkTestHost.SecretValue,
            },
        }, Ct);
        updated.IsSuccessStatusCode.Should().BeTrue(await updated.Content.ReadAsStringAsync(Ct));
        return id;
    }

    private static async Task<JsonElement> OkOrCreatedJsonAsync(HttpResponseMessage response)
    {
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task Reading_an_entrys_history_writes_one_row_naming_the_sensitive_field()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var admin = await _host.SuperAdminInAsync(tenant);
        var entry = await VersionedEntryAsync(admin, type);
        var before = (await RowsAsync(tenant, "content.sensitive.read")).Count;

        var history = await OkJsonAsync(await admin.GetAsync($"/api/contents/{entry}/history", Ct));
        var versions = history.GetProperty("items").EnumerateArray().ToList();
        versions.Should().HaveCount(2);
        versions.Should().AllSatisfy(v =>
            v.GetProperty("data").GetProperty("Secret").GetString().Should().Be(ShareLinkTestHost.SecretValue));

        var rows = await RowsAsync(tenant, "content.sensitive.read");
        rows.Should().HaveCount(before + 1, "one history read is one row, however many versions it sends");
        var row = rows.OrderByDescending(r => r.CreatedAt).First();
        row.TargetId.Should().Be(entry.ToString());
        row.Strings("fields").Should().Equal("Secret");
        row.Text("route").Should().Contain("history");
        row.Json().Should().NotContain(ShareLinkTestHost.SecretValue);
    }

    [Fact]
    public async Task A_rollback_that_returns_a_sensitive_field_writes_a_row()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var admin = await _host.SuperAdminInAsync(tenant);
        var entry = await VersionedEntryAsync(admin, type);
        var history = await OkJsonAsync(await admin.GetAsync($"/api/contents/{entry}/history", Ct));
        var first = history.GetProperty("items")[0].GetProperty("versionId").GetGuid();
        var before = (await RowsAsync(tenant, "content.sensitive.read")).Count;

        var rollback = await OkOrCreatedJsonAsync(
            await admin.PostAsync($"/api/contents/{entry}/rollback/{first}", null, Ct));
        rollback.GetProperty("data").GetProperty("Secret").GetString().Should().Be(ShareLinkTestHost.SecretValue);

        var rows = await RowsAsync(tenant, "content.sensitive.read");
        rows.Should().HaveCount(before + 1);
        var row = rows.OrderByDescending(r => r.CreatedAt).First();
        row.TargetId.Should().Be(entry.ToString());
        row.Text("route").Should().Contain("rollback");
        row.Strings("fields").Should().Equal("Secret");
    }

    [Fact]
    public async Task A_field_shown_as_its_last_four_characters_is_not_named()
    {
        var tenant = await _host.TenantAsync();
        var type = $"l4{Guid.NewGuid():n}"[..12];
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var id = Guid.NewGuid();
        await using (var session = store.LightweightSession(tenant))
        {
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields =
                [
                    new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                    new FieldDefinition
                    {
                        Name = "Card", DisplayName = "Card", Type = "string",
                        Sensitivity = SensitivityLevel.Sensitive, Mask = FieldMask.Last4,
                    },
                ],
            });
            session.Store(new Content
            {
                Id = id,
                ContentType = type,
                Data = new Dictionary<string, object> { ["Title"] = "card", ["Card"] = "4111111111111234" },
            });
            await session.SaveChangesAsync(Ct);
        }

        var reader = await _host.EditorInAsync(tenant, type, mayRead: true, mayUpdate: false);
        var body = await OkJsonAsync(await reader.GetAsync($"/api/contents/{id}", Ct));
        body.GetProperty("data").GetProperty("Card").GetString().Should().EndWith("1234").And.NotBe("4111111111111234");
        (await RowsAsync(tenant, "content.sensitive.read")).Should().BeEmpty("the last four were sent, not the value");

        var admin = await _host.SuperAdminInAsync(tenant);
        (await admin.GetAsync($"/api/contents/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await RowsAsync(tenant, "content.sensitive.read");
        rows.Should().HaveCount(1);
        rows[0].Strings("fields").Should().Equal("Card");
    }

    [Fact]
    public async Task A_read_made_with_an_api_key_names_the_key()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var entry = await _host.EntryAsync(tenant, type, "keyed");
        var userId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var username = $"key-{Guid.NewGuid():n}"[..14];
            session.Store(new User
            {
                Id = userId, Username = username, Email = $"{username}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = tenant,
                Status = MembershipStatus.Active, RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new ApiKey
            {
                Id = keyId,
                Name = "audit",
                KeyHash = barakoCMS.Infrastructure.Auth.ApiKeyService.Hash(secret),
                Prefix = secret[..12],
                UserId = userId,
                TenantSlug = tenant,
                Scopes = [ApiKeyScopes.ContentRead],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _host.AnonymousIn(tenant);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        var body = await OkJsonAsync(await client.GetAsync($"/api/contents/{entry}", Ct));
        body.GetProperty("data").GetProperty("Secret").GetString().Should().Be(ShareLinkTestHost.SecretValue);

        var rows = await RowsAsync(tenant, "content.sensitive.read");
        rows.Should().HaveCount(1);
        rows[0].ActorUserId.Should().Be(userId);
        rows[0].Text("apiKeyId").Should().Be(keyId.ToString());
        rows[0].Json().Should().NotContain(secret);
    }

    [Fact]
    public async Task Opening_an_entry_link_records_a_redemption_and_a_wrong_key_records_none()
    {
        var tenant = await _host.TenantAsync();
        var type = await _host.TypeAsync(tenant);
        var wip = await _host.EntryAsync(tenant, type, "wip");
        var admin = await _host.SuperAdminInAsync(tenant);
        var created = await ShareLinkTestHost.CreateEntryLinkAsync(admin, wip, new { label = "Client review" });
        var key = created.GetProperty("key").GetString()!;
        var linkId = created.GetProperty("id").GetGuid();

        (await _host.OpenAsync(tenant, key + "x")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await RowsAsync(tenant, "site.share_link.redeemed")).Should().BeEmpty("a 404 redeems nothing");

        (await _host.OpenAsync(tenant, key)).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await RowsAsync(tenant, "site.share_link.redeemed");
        rows.Should().HaveCount(1);
        rows[0].TargetId.Should().Be(linkId.ToString());
        rows[0].ActorUserId.Should().BeNull();
        rows[0].Text("via").Should().Be("open");
        rows[0].Text("scope").Should().Be("entry");
        rows[0].Text("entryId").Should().Be(wip.ToString());
        rows[0].Json().Should().NotContain(key).And.NotContain(ShareLinkTestHost.EntryHashHex(key));
    }

    [Fact]
    public async Task Redeeming_a_site_link_records_a_redemption_in_that_tenant_only()
    {
        var tenant = await _host.TenantAsync();
        var other = await _host.TenantAsync();
        var admin = await _host.SuperAdminInAsync(tenant);
        var created = await ShareLinkTestHost.CreateSiteLinkAsync(admin, "Holding page");
        var key = created.GetProperty("key").GetString()!;

        (await _host.RedeemAsync(other, key)).StatusCode.Should().Be(HttpStatusCode.NotFound, "another tenant's key");
        (await _host.RedeemAsync(tenant, key)).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await RowsAsync(tenant, "site.share_link.redeemed");
        rows.Should().HaveCount(1);
        rows[0].TargetId.Should().Be(created.GetProperty("id").GetGuid().ToString());
        rows[0].Text("via").Should().Be("redeem");
        rows[0].Text("scope").Should().Be("site");
        (await RowsAsync(other, "site.share_link.redeemed")).Should().BeEmpty();
    }
}
