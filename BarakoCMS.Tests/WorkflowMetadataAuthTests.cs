using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Both of these endpoints shipped with AllowAnonymous() and a "re-enable auth in production"
/// comment. /api/workflows/actions listed every registered action plugin and its parameters;
/// /api/workflows/variables read a real stored document of the requested content type and returned
/// its field names together with their stored values, applying no sensitivity masking. That was an
/// anonymous read of content the caller had no right to, and a bypass of the role restriction that
/// /api/schemas exists to enforce.
/// </summary>
[Collection("Sequential")]
public class WorkflowMetadataAuthTests
{
    private readonly IntegrationTestFixture _factory;

    public WorkflowMetadataAuthTests(IntegrationTestFixture factory)
    {
        _factory = factory;
    }

    private HttpClient AuthedAs(params string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken(roles));
        return client;
    }

    [Theory]
    [InlineData("/api/workflows/actions")]
    [InlineData("/api/workflows/variables")]
    public async Task Refuses_an_anonymous_caller(string route)
    {
        var response = await _factory.CreateClient().GetAsync(route);

        // Exactly 401. "Any non-200" would be satisfied by a 404, which would mean the route is
        // simply gone rather than protected.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/api/workflows/actions")]
    [InlineData("/api/workflows/variables")]
    public async Task Refuses_a_signed_in_caller_without_an_admin_role(string route)
    {
        var response = await AuthedAs("Editor").GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // The positive control for both tests above. Without it, deleting these endpoints or refusing
    // every caller would leave the refusal assertions green.
    [Theory]
    [InlineData("/api/workflows/actions")]
    [InlineData("/api/workflows/variables")]
    public async Task Still_answers_an_admin(string route)
    {
        var response = await AuthedAs("Admin").GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // POST /api/contents resolves the caller against a real User document, so a token whose UserId
    // matches no stored user is refused. Writing content therefore needs a user in the database,
    // not just a signed token with the right roles.
    private async Task<HttpClient> AuthedAsStoredUser(params string[] roleNames)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<Marten.IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var roleName in roleNames)
        {
            var role = await session.Query<barakoCMS.Models.Role>().FirstOrDefaultAsync(r => r.Name == roleName);
            if (role == null)
            {
                role = new barakoCMS.Models.Role
                {
                    Id = Guid.NewGuid(),
                    Name = roleName,
                    Permissions = new List<barakoCMS.Models.ContentTypePermission>(),
                };
                session.Store(role);
            }
            roleIds.Add(role.Id);
        }

        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"user-{userId}",
            Email = $"user-{userId}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roleNames, userId: userId.ToString()));
        return client;
    }

    [Fact]
    public async Task Does_not_echo_the_stored_value_of_a_data_field()
    {
        var admin = await AuthedAsStoredUser("SuperAdmin", "Admin");
        var typeName = $"secretcheck{Guid.NewGuid():N}"[..24];
        const string secret = "the-actual-stored-value-9f2b";

        var created = await admin.PostAsJsonAsync("/api/content-types", new
        {
            name = typeName,
            displayName = typeName,
            fields = new[]
            {
                new { name = "Notes", displayName = "Notes", type = "text", isRequired = false, validationRules = new { } },
            },
        });
        created.IsSuccessStatusCode.Should().BeTrue(
            $"the content type is the fixture for this test, but POST returned {created.StatusCode}");

        var stored = await admin.PostAsJsonAsync("/api/contents", new
        {
            contentType = typeName,
            data = new Dictionary<string, object> { ["Notes"] = secret },
        });
        stored.IsSuccessStatusCode.Should().BeTrue(
            $"the entry is the fixture for this test, but POST returned {stored.StatusCode}");

        var response = await admin.GetAsync($"/api/workflows/variables?contentType={typeName}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();

        // The field name is what a template author needs and must still be there — otherwise this
        // test would pass simply because the endpoint returned nothing at all.
        body.Should().Contain("data.Notes");
        body.Should().NotContain(secret);
    }
}
