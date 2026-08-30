using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Facts the admin was deriving for itself, now reported by the API.
/// </summary>
/// <remarks>
/// Each of these existed as a rule the server enforced and the client re-implemented from something
/// that was not the key. That is a drift with no detector: the two agree until somebody renames a
/// role or changes a policy, and the disagreement shows up as a button that does not work rather
/// than as a failure anybody sees.
///
/// The admin's own e2e suite cannot catch it either. It mocks every route, so the client's beliefs
/// about the contract are tested against the client's own fixtures. These are the server half,
/// asserted where the server actually answers.
/// </remarks>
[Collection("Sequential")]
public class AdminContractTests
{
    private readonly IntegrationTestFixture _factory;

    public AdminContractTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<HttpClient> SuperAdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var name in new[] { "SuperAdmin", "Admin" })
        {
            var role = await s.Query<Role>().FirstOrDefaultAsync(r => r.Name == name);
            if (role is null) { role = new Role { Id = Guid.NewGuid(), Name = name }; s.Store(role); }
            roleIds.Add(role.Id);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"contract_{Guid.NewGuid():n}",
            Email = $"contract_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds,
        };
        s.Store(user);
        await s.SaveChangesAsync();

        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin", "Admin"], userId: user.Id.ToString()));
        return c;
    }

    /// <summary>
    /// A seeded role reports isSystem, and one the operator made does not.
    /// </summary>
    /// <remarks>
    /// Both halves matter and the second is the one that was actually wrong. The admin blocked
    /// deletion by matching a name against a hardcoded list, so a custom role called "HR" was locked
    /// even though the server would delete it happily, and a renamed system role was offered for
    /// deletion the server refuses. The key is the seeded id, and only the server has it.
    /// </remarks>
    [Fact]
    public async Task The_roles_list_says_which_roles_the_server_refuses_to_delete()
    {
        var client = await SuperAdminAsync();

        var custom = new Role { Id = Guid.NewGuid(), Name = $"HR_{Guid.NewGuid():n}"[..12] };
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(custom);
            // A seeded role under a name nobody would guess, so a name-based check cannot pass.
            s.Store(new Role { Id = SystemRoles.HRRoleId, Name = "People Operations" });
            await s.SaveChangesAsync();
        }

        var body = await (await client.GetAsync("/api/roles?pageSize=100")).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();

        var renamed = items.First(i => i.GetProperty("id").GetGuid() == SystemRoles.HRRoleId);
        renamed.GetProperty("isSystem").GetBoolean().Should().BeTrue(
            "it is a seeded role whatever it has been renamed to, and the id is the key");

        var mine = items.First(i => i.GetProperty("id").GetGuid() == custom.Id);
        mine.GetProperty("isSystem").GetBoolean().Should().BeFalse(
            "a role an operator created is deletable however it is named");
    }

    /// <summary>
    /// The content list reports status, so a list can show what is a Draft.
    /// </summary>
    /// <remarks>
    /// The single-item GET returned it and the list did not, so the entries table either showed no
    /// status or cost a second request per row. Adding a field to a response is not a breaking
    /// change, which is why this could land without waiting for anything else.
    /// </remarks>
    [Fact]
    public async Task The_content_list_reports_status_and_sensitivity()
    {
        var client = await SuperAdminAsync();

        var type = $"listmeta_{Guid.NewGuid():n}"[..16];
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type,
                Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
            });
            s.Store(new Content
            {
                Id = Guid.NewGuid(), ContentType = type,
                Status = ContentStatus.Draft, Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["Title"] = "a draft" },
            });
            await s.SaveChangesAsync();
        }

        var body = await (await client.GetAsync($"/api/contents?contentType={type}")).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var item = doc.RootElement.GetProperty("items")[0];

        item.GetProperty("status").GetString().Should().Be("Draft",
            "a list with no status cannot show which entries are unpublished");
        item.GetProperty("sensitivity").GetString().Should().Be("Public");
    }

    /// <summary>
    /// The history endpoint answers with the same envelope as every other collection.
    /// </summary>
    /// <remarks>
    /// The admin read `versions` off this response and the endpoint has returned `items` since the
    /// envelope change, so the History panel rendered an empty list rather than failing visibly.
    /// Nothing caught it, because the e2e suite mocks the route and the mock was written to match
    /// the client.
    /// </remarks>
    [Fact]
    public async Task Content_history_uses_the_paginated_envelope()
    {
        var client = await SuperAdminAsync();

        var created = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = "Article",
            data = new Dictionary<string, object> { ["Title"] = "history envelope" },
        });
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            created.StatusCode, await created.Content.ReadAsStringAsync());

        using var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdDoc.RootElement.GetProperty("id").GetGuid();

        var body = await (await client.GetAsync($"/api/contents/{id}/history")).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        doc.RootElement.TryGetProperty("items", out var items).Should().BeTrue(
            "history is a collection like any other");
        doc.RootElement.TryGetProperty("versions", out _).Should().BeFalse(
            "the old key is gone, and a client still reading it gets undefined rather than an error");
        items.GetArrayLength().Should().BeGreaterThan(0);
    }
}
