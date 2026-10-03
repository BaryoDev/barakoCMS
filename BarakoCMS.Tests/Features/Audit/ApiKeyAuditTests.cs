using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using barakoCMS.Features.ApiKeys;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Audit;

/// <summary>
/// An API key acts as the user who made it, so making one and revoking one are grant changes. Neither
/// was recorded, which left "when was the leaked key revoked, and by whom" without an answer.
/// </summary>
[Collection("Sequential")]
public class ApiKeyAuditTests
{
    private readonly IntegrationTestFixture _factory;

    public ApiKeyAuditTests(IntegrationTestFixture factory) => _factory = factory;

    private static async Task<CreateApiKeyResponse> CreateKeyAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/api-keys", new
        {
            name,
            scopes = new[] { ApiKeyScopes.ContentRead },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CreateApiKeyResponse>())!;
    }

    [Fact]
    public async Task Creating_a_key_writes_a_row_that_holds_neither_the_key_nor_its_hash()
    {
        var slug = await AuditTenants.CreateAsync(_factory);
        var (client, adminId) = await AuditTenants.AdminAsync(_factory, slug);

        var key = await CreateKeyAsync(client, "deploy hook");

        var rows = await AuditRows.ForTargetAsync(_factory, "apikey.created", key.Id.ToString());

        rows.Should().HaveCount(1);
        var row = rows[0];
        row.TargetType.Should().Be("ApiKey");
        row.TenantSlug.Should().Be(slug);
        row.ActorUserId.Should().Be(adminId);
        row.Text("name").Should().Be("deploy hook");
        row.Text("actsAsUserId").Should().Be(adminId.ToString());
        row.Strings("scopes").Should().Equal(ApiKeyScopes.ContentRead);

        key.Key.Should().NotBeNullOrWhiteSpace("the assertions below need a secret to look for");
        var stored = row.Json();
        stored.Should().NotContain(key.Key);
        stored.Should().NotContain(ApiKeyService.Hash(key.Key));
        stored.Should().NotContain(key.Prefix);
    }

    [Fact]
    public async Task Revoking_a_key_writes_one_row_and_revoking_it_again_writes_none()
    {
        var slug = await AuditTenants.CreateAsync(_factory);
        var (client, adminId) = await AuditTenants.AdminAsync(_factory, slug);
        var key = await CreateKeyAsync(client, "to be revoked");

        (await client.DeleteAsync($"/api/api-keys/{key.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.DeleteAsync($"/api/api-keys/{key.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rows = await AuditRows.ForTargetAsync(_factory, "apikey.revoked", key.Id.ToString());

        rows.Should().HaveCount(1);
        rows[0].TenantSlug.Should().Be(slug);
        rows[0].ActorUserId.Should().Be(adminId);
        rows[0].Text("name").Should().Be("to be revoked");
        rows[0].Json().Should().NotContain(key.Key);
        rows[0].Json().Should().NotContain(ApiKeyService.Hash(key.Key));
    }

    /// <summary>
    /// Reading the audit log and managing keys are separate capabilities, so a caller with only the
    /// first learns that a key was made, by whom and what it is called, and not what it can do.
    /// </summary>
    [Fact]
    public async Task A_caller_who_cannot_manage_keys_is_shown_the_key_row_without_its_scopes()
    {
        var slug = await AuditTenants.CreateAsync(_factory);
        var (admin, adminId) = await AuditTenants.AdminAsync(_factory, slug);
        var key = await CreateKeyAsync(admin, "reporting");

        var viewer = await AuditTenants.HolderOfAsync(_factory, slug, SystemCapabilities.ViewAuditLog);
        var listed = await AuditRows.ListedAsync(viewer, "apikey.created", key.Id.ToString());

        listed.Should().HaveCount(1);
        listed[0].ActorUserId.Should().Be(adminId);
        listed[0].TargetType.Should().Be("ApiKey");
        listed[0].Metadata.Should().NotBeNull();
        listed[0].Metadata!.Keys.Should().Equal("name");
        listed[0].Metadata!["name"].ToString().Should().Be("reporting");

        var stored = await AuditRows.ForTargetAsync(_factory, "apikey.created", key.Id.ToString());
        stored.Should().HaveCount(1);
        stored[0].Strings("scopes").Should().Equal(new[] { ApiKeyScopes.ContentRead }, "the stored row is complete");
    }

    [Fact]
    public async Task A_caller_who_can_manage_keys_is_shown_the_key_row_in_full()
    {
        var slug = await AuditTenants.CreateAsync(_factory);
        var (admin, adminId) = await AuditTenants.AdminAsync(_factory, slug);
        var key = await CreateKeyAsync(admin, "reporting");

        var listed = await AuditRows.ListedAsync(admin, "apikey.created", key.Id.ToString());

        listed.Should().HaveCount(1);
        listed[0].Metadata.Should().NotBeNull();
        listed[0].Metadata!.Keys.Should().Contain(new[] { "name", "scopes", "contentTypes", "actsAsUserId" });
        listed[0].Metadata!["actsAsUserId"].ToString().Should().Be(adminId.ToString());
    }

    /// <summary>
    /// The list endpoint does not filter by action, so the new rows reach it with no filter to
    /// extend. What has to hold is the tenant boundary it already draws.
    /// </summary>
    [Fact]
    public async Task A_tenant_admin_lists_their_own_tenants_key_rows_and_not_another_tenants()
    {
        var mine = await AuditTenants.CreateAsync(_factory);
        var theirs = await AuditTenants.CreateAsync(_factory);
        var (myClient, _) = await AuditTenants.AdminAsync(_factory, mine);
        var (theirClient, _) = await AuditTenants.AdminAsync(_factory, theirs);

        var myKey = await CreateKeyAsync(myClient, "mine");
        var theirKey = await CreateKeyAsync(theirClient, "theirs");

        var response = await myClient.GetAsync("/api/audit?action=apikey.created");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var page = (await response.Content.ReadFromJsonAsync<
            PaginatedResponse<barakoCMS.Features.Audit.List.AuditEventDto>>())!;

        page.Items.Should().HaveCount(1);
        page.Items[0].TargetId.Should().Be(myKey.Id.ToString());
        page.Items[0].TenantSlug.Should().Be(mine);
        page.Items.Select(i => i.TargetId).Should().NotContain(theirKey.Id.ToString());

        var across = await myClient.GetAsync($"/api/audit?action=apikey.created&tenant={theirs}");
        across.StatusCode.Should().Be(HttpStatusCode.OK, await across.Content.ReadAsStringAsync());
        var acrossPage = (await across.Content.ReadFromJsonAsync<
            PaginatedResponse<barakoCMS.Features.Audit.List.AuditEventDto>>())!;

        acrossPage.Items.Should().HaveCount(1, "naming another tenant is not honoured for a tenant admin");
        acrossPage.Items[0].TargetId.Should().Be(myKey.Id.ToString());
    }
}
