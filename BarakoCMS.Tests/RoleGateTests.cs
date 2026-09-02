using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FastEndpoints;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Every core endpoint that declares a role or capability gate in <c>Configure()</c>, with the three cases
/// <see cref="WorkflowMetadataAuthTests"/> established: anonymous refused with 401, a signed-in
/// caller holding the wrong role refused with 403, and an admin still served.
///
/// The inventory below is not a hand-kept list that can drift. <see cref="The_inventory_matches_the_gates_the_running_host_declares"/>
/// reads the gates off the live routing table and fails when the two disagree, so adding a gated
/// endpoint without a refusal test, or dropping a gate from one that had it, breaks the build
/// instead of quietly reducing coverage. A gate is either <c>Roles(...)</c>/<c>Permissions(...)</c>
/// or the capability gate from issue #272, so migrating an endpoint from one to the other keeps it
/// in scope rather than dropping it out.
/// </summary>
[Collection("Sequential")]
public class RoleGateTests
{
    private readonly IntegrationTestFixture _factory;

    public RoleGateTests(IntegrationTestFixture factory)
    {
        _factory = factory;
    }

    /// <param name="Verb">HTTP method as routing reports it.</param>
    /// <param name="Route">Route template as declared, the key the structural test compares on.</param>
    /// <param name="Probe">A concrete path for that template. Route ids are deliberately unparseable
    /// so an admin is answered by request binding rather than by the handler, which keeps the
    /// positive control from creating or deleting anything.</param>
    public sealed record GatedRoute(string Verb, string Route, string Probe)
    {
        public string Key => $"{Verb} {Route}";

        public override string ToString() => $"{Verb} {Probe}";
    }

    private const string NotAGuid = "not-a-guid";

    /// <summary>Uppercase, so the connector slug check refuses it before any lookup happens.</summary>
    private const string NotASlug = "NOT_A_SLUG";

    public static readonly GatedRoute[] Inventory =
    [
        new("POST", "/api/api-keys", "/api/api-keys"),
        new("GET", "/api/api-keys", "/api/api-keys"),
        new("DELETE", "/api/api-keys/{id}", $"/api/api-keys/{NotAGuid}"),
        new("GET", "/api/audit", "/api/audit"),
        new("DELETE", "/api/contents/{id}/erase", $"/api/contents/{NotAGuid}/erase"),
        new("POST", "/api/contents/{id}/rollback/{versionId}", $"/api/contents/{NotAGuid}/rollback/{NotAGuid}"),
        new("POST", "/api/content-types", "/api/content-types"),
        new("GET", "/api/content-types", "/api/content-types"),
        new("GET", "/api/schemas", "/api/schemas"),
        new("PUT", "/api/content-types/{name}/public-delivery", "/api/content-types/no-such-type/public-delivery"),
        new("PUT", "/api/content-types/{name}/fields/{field}/sensitivity", "/api/content-types/no-such-type/fields/no-such-field/sensitivity"),
        new("POST", "/api/content-types/{name}/rebuild", "/api/content-types/no-such-type/rebuild"),
        new("GET", "/api/monitoring/health", "/api/monitoring/health"),
        new("GET", "/api/monitoring/k8s", "/api/monitoring/k8s"),
        new("GET", "/api/monitoring/metrics", "/api/monitoring/metrics"),
        new("POST", "/api/roles", "/api/roles"),
        new("GET", "/api/roles", "/api/roles"),
        new("GET", "/api/roles/{id}", $"/api/roles/{NotAGuid}"),
        new("PUT", "/api/roles/{id}", $"/api/roles/{NotAGuid}"),
        new("DELETE", "/api/roles/{id}", $"/api/roles/{NotAGuid}"),
        new("GET", "/api/settings", "/api/settings"),
        new("POST", "/api/settings", "/api/settings"),
        new("GET", "/api/connectors", "/api/connectors"),
        new("POST", "/api/connectors", "/api/connectors"),
        // NotASlug for the same reason ids here are unparseable: the endpoint answers 400 from its
        // own check, which proves routing reached it. A well formed slug would 404, and a 404 is
        // what a route that has been removed looks like too.
        new("GET", "/api/connectors/{slug}", $"/api/connectors/{NotASlug}"),
        new("PUT", "/api/connectors/{slug}", $"/api/connectors/{NotASlug}"),
        new("DELETE", "/api/connectors/{slug}", $"/api/connectors/{NotASlug}"),
        new("POST", "/api/connectors/{slug}/test", $"/api/connectors/{NotASlug}/test"),
        new("GET", "/api/settings/email", "/api/settings/email"),
        new("PUT", "/api/settings/email", "/api/settings/email"),
        new("POST", "/api/settings/email/test", "/api/settings/email/test"),
        new("GET", "/api/tenants", "/api/tenants"),
        new("POST", "/api/tenants", "/api/tenants"),
        new("PUT", "/api/tenants/{handle}", "/api/tenants/no-such-tenant"),
        new("GET", "/api/tenants/members", "/api/tenants/members"),
        new("POST", "/api/tenants/members", "/api/tenants/members"),
        new("PUT", "/api/tenants/members/{userId}", $"/api/tenants/members/{NotAGuid}"),
        new("DELETE", "/api/tenants/members/{userId}", $"/api/tenants/members/{NotAGuid}"),
        new("GET", "/api/tenants/members/roles", "/api/tenants/members/roles"),
        new("GET", "/api/user-groups", "/api/user-groups"),
        new("POST", "/api/user-groups", "/api/user-groups"),
        new("GET", "/api/user-groups/{id}", $"/api/user-groups/{NotAGuid}"),
        new("PUT", "/api/user-groups/{id}", $"/api/user-groups/{NotAGuid}"),
        new("DELETE", "/api/user-groups/{id}", $"/api/user-groups/{NotAGuid}"),
        new("POST", "/api/user-groups/{groupId}/users", $"/api/user-groups/{NotAGuid}/users"),
        new("DELETE", "/api/user-groups/{groupId}/users/{userId}", $"/api/user-groups/{NotAGuid}/users/{NotAGuid}"),
        new("GET", "/api/users", "/api/users"),
        new("POST", "/api/users/{userId}/groups", $"/api/users/{NotAGuid}/groups"),
        new("DELETE", "/api/users/{userId}/groups/{groupId}", $"/api/users/{NotAGuid}/groups/{NotAGuid}"),
        new("POST", "/api/users/{userId}/roles", $"/api/users/{NotAGuid}/roles"),
        new("DELETE", "/api/users/{userId}/roles/{roleId}", $"/api/users/{NotAGuid}/roles/{NotAGuid}"),
        new("POST", "/api/users/{userId}/password", $"/api/users/{NotAGuid}/password"),
        new("GET", "/api/workflows", "/api/workflows"),
        new("POST", "/api/workflows", "/api/workflows"),
        new("GET", "/api/workflows/actions", "/api/workflows/actions"),
        new("GET", "/api/workflows/variables", "/api/workflows/variables"),
        new("POST", "/api/workflows/validate", "/api/workflows/validate"),
        new("POST", "/api/workflows/dry-run", "/api/workflows/dry-run"),
        new("GET", "/api/workflows/{id}/debug", $"/api/workflows/{NotAGuid}/debug"),
    ];

    public static TheoryData<GatedRoute> AllGatedRoutes()
    {
        var data = new TheoryData<GatedRoute>();
        foreach (var route in Inventory)
            data.Add(route);
        return data;
    }

    /// <summary>
    /// The structural half of this file. It asks the running host which core endpoints carry a role
    /// gate and compares that with <see cref="Inventory"/>, which is also what drives the three
    /// behavioural theories below. So a gated endpoint with no refusal coverage cannot exist: it is
    /// either in the inventory, and therefore tested, or this test fails naming it.
    /// </summary>
    [Fact]
    public void The_inventory_matches_the_gates_the_running_host_declares()
    {
        var declared = GatedRoutesFromTheRunningHost();

        // Without this the comparison could be satisfied by reading no endpoints at all, which is
        // the failure mode where the reflection below stops finding anything and every route
        // silently drops out of scope.
        declared.Should().NotBeEmpty("the host serves gated endpoints, so reading none means this test stopped looking");

        declared.Should().BeEquivalentTo(
            Inventory.Select(r => r.Key),
            "a core endpoint declaring Roles(...) or a capability gate must appear in RoleGateTests.Inventory, which is what gives it the 401/403/served treatment");
    }

    [Theory]
    [MemberData(nameof(AllGatedRoutes))]
    public async Task Refuses_an_anonymous_caller(GatedRoute route)
    {
        var response = await _factory.CreateClient().SendAsync(Request(route));

        // Exactly 401. "Any non-200" would be satisfied by a 404, which would mean the route is
        // simply gone rather than protected.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(AllGatedRoutes))]
    public async Task Refuses_a_signed_in_caller_without_an_admin_role(GatedRoute route)
    {
        var client = await WrongRoleClient();

        var response = await client.SendAsync(Request(route));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The positive control. Without it a gate that refuses everyone, or a route that no longer
    /// exists, would leave both refusal theories green.
    /// </summary>
    /// <remarks>
    /// The admin is refused nothing and reaches the endpoint: 401 and 403 mean the gate turned an
    /// allowed role away, and 404 means routing found nothing to run. What comes back instead is
    /// usually a 400 from binding the deliberately unparseable id or body, which only the endpoint
    /// itself can produce, and which mutates nothing.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllGatedRoutes))]
    public async Task Still_answers_an_admin(GatedRoute route)
    {
        var client = await AdminClient();

        var response = await client.SendAsync(Request(route));

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "an admin holding the gated role must not be turned away by authentication");
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "a gate that refuses an admin too would make the two refusal tests pass for the wrong reason");
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            "routing must still reach an endpoint here, otherwise the refusals above only prove the route is gone");
    }

    private static HttpRequestMessage Request(GatedRoute route)
    {
        var request = new HttpRequestMessage(new HttpMethod(route.Verb), route.Probe);

        if (route.Verb is "POST" or "PUT" or "PATCH")
        {
            // Deliberately not valid JSON. Authorization runs before model binding, so this changes
            // nothing about what the two refusal tests see, and it stops the admin case from
            // writing anything to the shared database.
            request.Content = new StringContent("{", Encoding.UTF8, "application/json");
        }

        return request;
    }

    // One caller of each kind for the whole class, rather than one per theory case. The database is
    // shared with every other test in the collection, and a hundred throwaway users would sit at
    // the top of GET /api/users, where other tests read the first row.
    private static readonly Lock Gate = new();
    private static Task<HttpClient>? _admin;
    private static Task<HttpClient>? _wrongRole;

    private Task<HttpClient> AdminClient()
    {
        lock (Gate) return _admin ??= AuthedAsStoredUser("SuperAdmin", "Admin");
    }

    private Task<HttpClient> WrongRoleClient()
    {
        lock (Gate) return _wrongRole ??= AuthedAsStoredUser("User");
    }

    /// <summary>
    /// Roles are resolved from the stored <see cref="barakoCMS.Models.User"/> and its memberships,
    /// not from the token alone, so a caller that has to pass anything beyond the FastEndpoints
    /// gate needs a real user document holding real role ids.
    /// </summary>
    private async Task<HttpClient> AuthedAsStoredUser(params string[] roleNames)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

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
            Username = $"gate-{userId}",
            Email = $"gate-{userId}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roleNames, userId: userId.ToString()));
        return client;
    }

    private IReadOnlyList<string> GatedRoutesFromTheRunningHost()
    {
        var core = typeof(Program).Assembly;

        return _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => new
            {
                endpoint.RoutePattern,
                Definition = endpoint.Metadata.OfType<EndpointDefinition>().FirstOrDefault(),
                Capability = endpoint.Metadata.GetMetadata<barakoCMS.Infrastructure.Auth.RequiredCapability>(),
                Methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
            })
            .Where(x => x.Definition is not null && x.Definition.EndpointType.Assembly == core)
            .Where(x => x.Definition!.AllowedRoles?.Count > 0
                        || x.Definition.AllowedPermissions?.Count > 0
                        || x.Capability is not null)
            .SelectMany(x => x.Methods
                .Where(method => x.Definition!.AnonymousVerbs?.Contains(method) != true)
                .Select(method => $"{method} /{x.RoutePattern.RawText?.TrimStart('/')}"))
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }
}
