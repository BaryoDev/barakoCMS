using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// What a create or update request is allowed to say about status and sensitivity.
/// </summary>
/// <remarks>
/// Two separate faults, both from a non-nullable enum bound straight off the wire.
///
/// An undefined number bound cleanly, so <c>{"status": 7}</c> stored content with a status no
/// member names. That content is invisible to the scheduler, to status-filtered lists and to public
/// delivery, and nothing reported an error. ChangeStatus has validated <c>IsInEnum</c> since it was
/// written; Create and Update did not.
///
/// An omitted status bound to 0, which is Draft, and Update treated any difference from the stored
/// status as a transition. A consumer sending only id, data and version, which is what a data-only
/// edit looks like, un-published the item and emitted a ContentStatusChanged saying so.
/// </remarks>
[Collection("Sequential")]
public class ContentStatusBindingTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ContentStatusBindingTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Create_refuses_a_status_no_enum_member_names()
    {
        await AuthenticateAsync();
        var typeName = await CreateContentTypeAsync();

        var response = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = typeName,
            data = new Dictionary<string, object> { ["Title"] = "undefined status" },
            status = 7,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "content stored with an undefined status is invisible to the scheduler, to status-filtered lists and to delivery");
    }

    [Fact]
    public async Task Create_refuses_a_sensitivity_no_enum_member_names()
    {
        await AuthenticateAsync();
        var typeName = await CreateContentTypeAsync();

        var response = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = typeName,
            data = new Dictionary<string, object> { ["Title"] = "undefined sensitivity" },
            sensitivity = 9,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a sensitivity outside the enum is not Public, Sensitive or Hidden, so nothing can decide what to mask");
    }

    /// <summary>The defined values keep working, so the rule is a floor and not a ban.</summary>
    [Fact]
    public async Task Create_still_accepts_a_defined_status_sent_as_a_number()
    {
        await AuthenticateAsync();
        var typeName = await CreateContentTypeAsync();

        var response = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = typeName,
            data = new Dictionary<string, object> { ["Title"] = "numeric but defined" },
            status = 1,
        });

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Update_refuses_a_status_no_enum_member_names()
    {
        await AuthenticateAsync();
        var (id, version) = await CreateContentAsync("Published");

        var response = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "edited" },
            status = 7,
            version,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The data-only edit: id, data and version, no status field at all.
    /// </summary>
    [Fact]
    public async Task An_update_that_omits_status_leaves_the_content_published()
    {
        await AuthenticateAsync();
        var (id, version) = await CreateContentAsync("Published");

        var response = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "a data-only edit" },
            version,
        });

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync());

        (await StatusOfAsync(id)).Should().Be("Published",
            "an absent status means unchanged; binding it to Draft un-published the item and told everyone downstream it had been unpublished on purpose");
    }

    /// <summary>An update that does send a status still moves the content.</summary>
    [Fact]
    public async Task An_update_that_sends_a_status_still_changes_it()
    {
        await AuthenticateAsync();
        var (id, version) = await CreateContentAsync("Published");

        var response = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "now archived" },
            status = "Archived",
            version,
        });

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync());

        (await StatusOfAsync(id)).Should().Be("Archived");
    }

    private async Task<string?> StatusOfAsync(Guid id)
    {
        var response = await _client.GetAsync($"/api/contents/{id}");
        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("status").GetString();
    }

    private async Task<string> CreateContentTypeAsync()
    {
        var typeName = "status-bind-" + Guid.NewGuid().ToString("n")[..8];
        var response = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = typeName,
            displayName = "Status Bind",
            fields = new[] { new { name = "Title", type = "Text" } },
        });
        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync());
        return typeName;
    }

    private async Task<(Guid Id, long Version)> CreateContentAsync(string status)
    {
        var typeName = await CreateContentTypeAsync();
        var created = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = typeName,
            data = new Dictionary<string, object> { ["Title"] = "before the edit" },
            status,
        });
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            created.StatusCode, await created.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return (document.RootElement.GetProperty("id").GetGuid(),
                document.RootElement.GetProperty("version").GetInt64());
    }

    private async Task AuthenticateAsync()
    {
        string[] roles = ["SuperAdmin", "Admin"];

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var roleName in roles)
        {
            var role = await session.Query<barakoCMS.Models.Role>().FirstOrDefaultAsync(r => r.Name == roleName);
            if (role is null)
            {
                role = new barakoCMS.Models.Role { Id = Guid.NewGuid(), Name = roleName };
                session.Store(role);
            }

            roleIds.Add(role.Id);
        }

        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"status-bind-{userId:n}",
            Email = $"status-bind-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync();

        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roles, userId: userId.ToString()));
    }
}
