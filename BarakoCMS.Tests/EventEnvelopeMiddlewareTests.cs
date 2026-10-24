using System.Net;
using System.Net.Http.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// X-Branch-Id names the request's branch, only a branch the caller holds, and a branch scoped type
/// refuses a create without one.
/// </summary>
[Collection("Integration1")]
public class EventEnvelopeMiddlewareTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly WebApplicationFactory<Program> _host;

    public EventEnvelopeMiddlewareTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _host = factory.WithSettings(new Dictionary<string, string?> { ["Barako:NodeId"] = Guid.NewGuid().ToString() });
    }

    private async Task<string> TypeAsync(bool branchScoped)
    {
        using var scope = _host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var name = "ward" + Guid.NewGuid().ToString("n")[..8];
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Ward",
            BranchScoped = branchScoped,
            Fields = [new() { Name = "Title", DisplayName = "Title", Type = "string" }],
        });
        await session.SaveChangesAsync();
        return name;
    }

    /// <summary>An admin, holding the branches named in their membership profile, or no attribute.</summary>
    private async Task<HttpClient> AdminAsync(params Guid[] branches)
    {
        using var scope = _host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var name in new[] { "SuperAdmin", "Admin" })
        {
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == name);
            if (role is null) { role = new Role { Id = Guid.NewGuid(), Name = name }; session.Store(role); }
            roleIds.Add(role.Id);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"envadmin_{Guid.NewGuid():n}",
            Email = $"envadmin_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds,
        };
        session.Store(user);
        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TenantSlug = Tenant.DefaultSlug,
            Status = MembershipStatus.Active,
            RoleIds = roleIds,
            Profile = branches.Length == 0
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["branches"] = string.Join(",", branches) },
        });
        await session.SaveChangesAsync();

        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin", "Admin"], userId: user.Id.ToString()));
        return client;
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string type, string? branch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/contents")
        {
            Content = JsonContent.Create(new
            {
                contentType = type,
                data = new Dictionary<string, object> { ["Title"] = "a ward" },
            }),
        };
        if (branch is not null) request.Headers.Add("X-Branch-Id", branch);
        return client.SendAsync(request);
    }

    private async Task<Guid?> StoredBranchAsync(HttpResponseMessage response)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetGuid();
        using var scope = _host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(id))!.BranchId;
    }

    [Fact]
    public async Task Create_without_a_branch_on_a_branch_scoped_type_is_400_with_the_text()
    {
        var type = await TypeAsync(branchScoped: true);
        var branch = Guid.NewGuid();
        var client = await AdminAsync(branch);

        var refused = await CreateAsync(client, type, branch: null);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("A branch is required.");

        var created = await CreateAsync(client, type, branch.ToString());
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        (await StoredBranchAsync(created)).Should().Be(branch);
    }

    [Fact]
    public async Task A_bad_branch_header_is_400()
    {
        var client = await AdminAsync(Guid.NewGuid());
        var res = await CreateAsync(client, await TypeAsync(branchScoped: true), "not-a-guid");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("X-Branch-Id is not a GUID.");
    }

    [Fact]
    public async Task A_caller_may_use_any_branch_in_their_list()
    {
        var b1 = Guid.NewGuid();
        var b2 = Guid.NewGuid();
        var client = await AdminAsync(b1, b2);

        var res = await CreateAsync(client, await TypeAsync(branchScoped: true), b2.ToString());
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await StoredBranchAsync(res)).Should().Be(b2);
    }

    [Fact]
    public async Task A_header_outside_the_list_is_403()
    {
        var client = await AdminAsync(Guid.NewGuid(), Guid.NewGuid());
        var res = await CreateAsync(client, await TypeAsync(branchScoped: true), Guid.NewGuid().ToString());
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Contain("X-Branch-Id is not one of your branches.");
    }

    [Fact]
    public async Task A_caller_with_no_branches_attribute_is_403_when_a_header_is_sent()
    {
        var client = await AdminAsync();
        var res = await CreateAsync(client, await TypeAsync(branchScoped: false), Guid.NewGuid().ToString());
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_header_on_a_request_that_is_not_signed_in_is_not_refused_as_a_branch_error()
    {
        var anonymous = _host.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Branch-Id", Guid.NewGuid().ToString());

        var res = await anonymous.PostAsJsonAsync("/api/auth/login", new { username = "nobody", password = "wrong" });

        (await res.Content.ReadAsStringAsync()).Should().NotContain("X-Branch-Id");
        res.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_type_that_is_not_branch_scoped_creates_without_a_header()
    {
        var client = await AdminAsync();
        var res = await CreateAsync(client, await TypeAsync(branchScoped: false), branch: null);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await StoredBranchAsync(res)).Should().BeNull();
    }
}
