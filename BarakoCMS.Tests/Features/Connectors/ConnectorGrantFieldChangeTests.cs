using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BarakoCMS.Tests.Features.Config;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// A field or a content type that a connector's grant names cannot be removed or renamed until the
/// grant is taken off, and the refusal names the connector (#1423).
/// </summary>
[Collection("Integration1")]
public class ConnectorGrantFieldChangeTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int _ip;

    private static string NextIp() => $"198.51.100.{Interlocked.Increment(ref _ip) % 250 + 1}";

    private async Task<HttpClient> AdminAsync()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    private async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync(Ct);

    [Fact]
    public async Task Removing_a_field_a_connector_is_granted_is_refused_with_409_naming_the_connector()
    {
        var subject = await GrantKit.SeedAsync(fixture, ["Ssn"]);

        var response = await (await AdminAsync()).PostAsJsonAsync(
            $"/api/content-types/{subject.Type}/fields/Ssn/change", new { remove = true }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await BodyAsync(response));
        (await BodyAsync(response)).Should().Contain(subject.ConnectorSlug).And.Contain(subject.Type + ".Ssn");
        (await FieldNamesAsync(subject.Type)).Should().Contain("Ssn");
    }

    [Fact]
    public async Task Renaming_a_field_a_connector_is_granted_is_refused_with_409_naming_the_connector()
    {
        var subject = await GrantKit.SeedAsync(fixture, ["Perk"]);

        var response = await (await AdminAsync()).PostAsJsonAsync(
            $"/api/content-types/{subject.Type}/fields/Perk/change", new { newName = "Bonus" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await BodyAsync(response));
        (await BodyAsync(response)).Should().Contain(subject.ConnectorSlug);
        (await FieldNamesAsync(subject.Type)).Should().Contain("Perk").And.NotContain("Bonus");
    }

    [Fact]
    public async Task A_grant_is_matched_ignoring_case_when_a_field_is_removed()
    {
        var subject = await GrantKit.SeedAsync(fixture, []);
        await RequestKit.SetGrantsAsync(fixture, subject, [subject.Type.ToUpperInvariant() + ".SSN"]);

        var response = await (await AdminAsync()).PostAsJsonAsync(
            $"/api/content-types/{subject.Type}/fields/Ssn/change", new { remove = true }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await BodyAsync(response));
    }

    private async Task<List<string>> FieldNamesAsync(string type)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<ContentTypeDefinition>().SingleAsync(t => t.Name == type, Ct)).Fields.Select(f => f.Name).ToList();
    }

    [Fact]
    public async Task A_config_apply_that_drops_a_granted_field_is_refused_and_the_type_keeps_it()
    {
        var callers = new ConfigCallers(fixture);
        var tenant = await callers.TenantAsync();
        var type = TestNames.Compact("gr");
        await callers.StoreInTenantAsync(tenant, new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Ssn", DisplayName = "Ssn", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            ],
        }, new Connector
        {
            Id = Guid.NewGuid(), Name = "Granted", Slug = "granted-link", BaseUrl = "https://egress.example",
            Auth = ConnectorAuth.None, AllowedFields = [type + ".Ssn"],
        });
        var client = await callers.SuperAdminAsync(tenant);
        var (_, exported) = await ConfigCallers.ExportAsync(client, "contentTypes");
        var document = JsonNode.Parse(exported.GetRawText())!;
        var fields = document["contentTypes"]![type]!["fields"]!.AsArray();
        foreach (var gone in fields.Where(f => f!["name"]!.GetValue<string>() == "Ssn").ToList()) fields.Remove(gone);

        var (response, body) = await ConfigCallers.ApplyAsync(client, document, allowDestructive: true);

        response.IsSuccessStatusCode.Should().BeFalse(body.ToString());
        body.ToString().Should().Contain("granted-link");
        var stored = await callers.InTenantAsync(tenant, s => s.Query<ContentTypeDefinition>().SingleAsync(t => t.Name == type, Ct));
        stored!.Fields.Select(f => f.Name).Should().Contain("Ssn");
    }

    // The shared types below belong to an account, and every tenant of the account uses them.

    private async Task StoreAsync(params object[] documents) => await GrantKit.StoreAsync(fixture, documents);

    private async Task StoreInTenantAsync(string tenant, params object[] documents)
    {
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<(TenantAccount Account, string Inside, string Outside, HttpClient Manager, string Type)> AccountAsync()
    {
        var account = new TenantAccount { Id = Guid.NewGuid(), Slug = GrantKit.Name("ga"), Name = "Grants" };
        var inside = GrantKit.Name("gi");
        var outside = GrantKit.Name("go");
        var userId = Guid.NewGuid();
        await StoreAsync(
            account,
            new Tenant { Id = Guid.NewGuid(), Slug = inside, Name = inside, AccountId = account.Id },
            new Tenant { Id = Guid.NewGuid(), Slug = outside, Name = outside },
            new User { Id = userId, Username = $"gm-{userId:N}"[..14], Email = $"gm-{userId:N}@example.com", RoleIds = [] },
            new TenantAccountManager { Id = Guid.NewGuid(), AccountId = account.Id, UserId = userId });

        var manager = fixture.CreateClient();
        manager.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.CreateToken(roles: [$"Account Manager {userId:N}"], userId: userId.ToString()));

        var type = GrantKit.Name("room");
        var created = await manager.PostAsJsonAsync($"/api/accounts/{account.Slug}/content-types", new
        {
            name = type, displayName = "Room",
            fields = new[] { new { name = "Title", displayName = "Title", type = "string", isRequired = true } },
        }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(created));
        return (account, inside, outside, manager, type);
    }

    private static Connector Granting(string type) => new()
    {
        Id = Guid.NewGuid(), Name = "Granted", Slug = "room-link", BaseUrl = "https://egress.example",
        Auth = ConnectorAuth.None, AllowedFields = [type + ".Title"],
    };

    [Fact]
    public async Task Deleting_a_content_type_a_connector_is_granted_is_refused_with_409_naming_the_connector()
    {
        var (account, inside, _, manager, type) = await AccountAsync();
        await StoreInTenantAsync(inside, Granting(type));

        var response = await manager.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{type}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await BodyAsync(response));
        (await BodyAsync(response)).Should().Contain("room-link");
        (await manager.GetAsync($"/api/accounts/{account.Slug}/content-types", Ct)).Content.ReadAsStringAsync(Ct).Result
            .Should().Contain(type);
    }

    [Fact]
    public async Task A_grant_in_a_tenant_outside_the_account_does_not_stop_the_account_deleting_its_type()
    {
        var (account, _, outside, manager, type) = await AccountAsync();
        await StoreInTenantAsync(outside, Granting(type));

        var response = await manager.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{type}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await BodyAsync(response));
    }

    [Fact]
    public async Task A_connector_with_the_same_slug_but_no_grant_in_another_tenant_of_the_account_does_not_stop_the_delete()
    {
        var (account, inside, _, manager, type) = await AccountAsync();
        var plain = Granting(type);
        plain.AllowedFields = null;
        await StoreInTenantAsync(inside, plain);

        var response = await manager.DeleteAsync($"/api/accounts/{account.Slug}/content-types/{type}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await BodyAsync(response));
    }
}
