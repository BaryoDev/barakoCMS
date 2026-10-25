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
[Collection("Integration1")]
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

    /// <summary>
    /// Over the length the module configuration route accepts, so an admin is answered by request
    /// validation (400) rather than by a 404 for a module the shared host does not have.
    /// </summary>
    private static readonly string TooLongForAModuleName = new('m', 101);

    /// <summary>Uppercase, so the connector slug check refuses it before any lookup happens.</summary>
    private const string NotASlug = "NOT_A_SLUG";

    public static readonly GatedRoute[] Inventory =
    [
        new("POST", "/api/api-keys", "/api/api-keys"),
        new("GET", "/api/api-keys", "/api/api-keys"),
        new("DELETE", "/api/api-keys/{id}", $"/api/api-keys/{NotAGuid}"),
        new("GET", "/api/audit", "/api/audit"),
        new("GET", "/api/jobs", "/api/jobs"),
        new("DELETE", "/api/contents/{id}/erase", $"/api/contents/{NotAGuid}/erase"),
        new("POST", "/api/contents/{id}/rollback/{versionId}", $"/api/contents/{NotAGuid}/rollback/{NotAGuid}"),
        new("GET", "/api/contents/{id}/runs", $"/api/contents/{NotAGuid}/runs"),
        new("POST", "/api/content-types", "/api/content-types"),
        new("GET", "/api/content-types", "/api/content-types"),
        new("GET", "/api/schemas", "/api/schemas"),
        new("PUT", "/api/content-types/{name}/public-delivery", "/api/content-types/no-such-type/public-delivery"),
        new("PUT", "/api/content-types/{name}/fields/{field}/sensitivity", "/api/content-types/no-such-type/fields/no-such-field/sensitivity"),
        new("PUT", "/api/content-types/{name}/fields/{field}/options", "/api/content-types/no-such-type/fields/no-such-field/options"),
        new("PUT", "/api/content-types/{name}/fields/{field}/currency", "/api/content-types/no-such-type/fields/no-such-field/currency"),
        new("PUT", "/api/content-types/{name}/fields/{field}/presentation", "/api/content-types/no-such-type/fields/no-such-field/presentation"),
        new("PUT", "/api/content-types/{name}/route-template", "/api/content-types/no-such-type/route-template"),
        new("PUT", "/api/content-types/{name}/icon", "/api/content-types/no-such-type/icon"),
        new("PUT", "/api/content-types/{name}/structured-data", "/api/content-types/no-such-type/structured-data"),
        new("PUT", "/api/content-types/{name}/uniqueness", "/api/content-types/no-such-type/uniqueness"),
        new("PUT", "/api/content-types/{name}/lookup", "/api/content-types/no-such-type/lookup"),
        new("PUT", "/api/content-types/{name}/lines", "/api/content-types/no-such-type/lines"),
        new("GET", "/api/content-types/{name}/uniqueness/{rule}/duplicates", "/api/content-types/no-such-type/uniqueness/NoSuchRule/duplicates?page=not-a-number"),
        new("POST", "/api/content-types/{name}/fields", "/api/content-types/no-such-type/fields"),
        new("POST", "/api/content-types/{name}/rebuild", "/api/content-types/no-such-type/rebuild"),
        new("GET", "/api/content-types/blueprints", "/api/content-types/blueprints"),
        new("POST", "/api/content-types/blueprints/{name}", "/api/content-types/blueprints/no-such-blueprint"),
        new("GET", "/api/monitoring/health", "/api/monitoring/health"),
        new("GET", "/api/monitoring/k8s", "/api/monitoring/k8s"),
        new("GET", "/api/monitoring/metrics", "/api/monitoring/metrics"),
        new("GET", "/api/modules", "/api/modules"),
        new("GET", "/api/modules/{name}/configuration", $"/api/modules/{TooLongForAModuleName}/configuration"),
        new("GET", "/api/capabilities", "/api/capabilities"),
        new("GET", "/api/config/export", "/api/config/export"),
        new("POST", "/api/config/plan", "/api/config/plan"),
        new("POST", "/api/config/apply", "/api/config/apply"),
        new("POST", "/api/roles", "/api/roles"),
        new("GET", "/api/roles", "/api/roles"),
        new("GET", "/api/roles/{id}", $"/api/roles/{NotAGuid}"),
        new("PUT", "/api/roles/{id}", $"/api/roles/{NotAGuid}"),
        new("DELETE", "/api/roles/{id}", $"/api/roles/{NotAGuid}"),
        new("GET", "/api/settings", "/api/settings"),
        new("POST", "/api/settings", "/api/settings"),
        new("GET", "/api/requests", "/api/requests"),
        new("POST", "/api/requests", "/api/requests"),
        new("GET", "/api/requests/{slug}", $"/api/requests/{NotASlug}"),
        new("DELETE", "/api/requests/{slug}", $"/api/requests/{NotASlug}"),
        new("POST", "/api/requests/{slug}/dry-run/{contentId}", $"/api/requests/{NotASlug}/dry-run/{NotAGuid}"),
        new("POST", "/api/content-types/{name}/seo-fields", $"/api/content-types/{NotASlug}/seo-fields"),
        new("GET", "/api/redirects", "/api/redirects"),
        new("POST", "/api/redirects", "/api/redirects"),
        new("DELETE", "/api/redirects/{id}", $"/api/redirects/{NotAGuid}"),
        new("POST", "/api/redirects/import", "/api/redirects/import"),
        new("GET", "/api/queries", "/api/queries"),
        new("POST", "/api/queries", "/api/queries"),
        new("GET", "/api/queries/{slug}", $"/api/queries/{NotASlug}"),
        new("DELETE", "/api/queries/{slug}", $"/api/queries/{NotASlug}"),
        new("POST", "/api/queries/{slug}/preview", $"/api/queries/{NotASlug}/preview"),
        new("POST", "/api/queries/{slug}/run", $"/api/queries/{NotASlug}/run"),
        new("GET", "/api/webhook-deliveries", "/api/webhook-deliveries"),
        new("GET", "/api/connector-deliveries", "/api/connector-deliveries"),
        new("GET", "/api/workflow-runs", "/api/workflow-runs"),
        new("GET", "/api/workflow-runs/{id}", $"/api/workflow-runs/{NotAGuid}"),
        new("POST", "/api/workflow-runs/{id}/actions/{ordinal}/retry", $"/api/workflow-runs/{NotAGuid}/actions/0/retry"),
        new("POST", "/api/workflow-runs/{id}/cancel", $"/api/workflow-runs/{NotAGuid}/cancel"),
        new("POST", "/api/workflow-runs/{id}/abandon", $"/api/workflow-runs/{NotAGuid}/abandon"),
        new("GET", "/api/connectors", "/api/connectors"),
        new("POST", "/api/connectors", "/api/connectors"),
        // NotASlug for the same reason ids here are unparseable: the endpoint answers 400 from its
        // own check, which proves routing reached it. A well formed slug would 404, and a 404 is
        // what a route that has been removed looks like too.
        new("GET", "/api/connectors/{slug}", $"/api/connectors/{NotASlug}"),
        new("PUT", "/api/connectors/{slug}", $"/api/connectors/{NotASlug}"),
        new("DELETE", "/api/connectors/{slug}", $"/api/connectors/{NotASlug}"),
        new("POST", "/api/connectors/{slug}/test", $"/api/connectors/{NotASlug}/test"),
        new("GET", "/api/collection-syncs", "/api/collection-syncs"),
        new("POST", "/api/collection-syncs", "/api/collection-syncs"),
        new("GET", "/api/collection-syncs/{slug}", $"/api/collection-syncs/{NotASlug}"),
        new("PUT", "/api/collection-syncs/{slug}", $"/api/collection-syncs/{NotASlug}"),
        new("DELETE", "/api/collection-syncs/{slug}", $"/api/collection-syncs/{NotASlug}"),
        new("POST", "/api/collection-syncs/{slug}/run", $"/api/collection-syncs/{NotASlug}/run"),
        new("GET", "/api/settings/email", "/api/settings/email"),
        new("PUT", "/api/settings/email", "/api/settings/email"),
        new("POST", "/api/settings/email/test", "/api/settings/email/test"),
        new("GET", "/api/settings/locales", "/api/settings/locales"),
        new("PUT", "/api/settings/locales", "/api/settings/locales"),
        new("GET", "/api/tenants", "/api/tenants"),
        new("POST", "/api/tenants", "/api/tenants"),
        new("PUT", "/api/tenants/{handle}", "/api/tenants/no-such-tenant"),
        new("GET", "/api/tenants/{handle}/export", $"/api/tenants/{NotASlug}/export"),
        new("POST", "/api/tenants/import", "/api/tenants/import"),
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
        new("POST", "/api/users/{userId}/mfa/reset", $"/api/users/{NotAGuid}/mfa/reset"),
        // to before from: the endpoint answers 400 from its own range check, before any ledger read.
        new("GET", "/api/numbering/external/{series}/gaps", "/api/numbering/external/no-such-series/gaps?from=5&to=1"),
        new("GET", "/api/stations", "/api/stations"),
        new("POST", "/api/stations", "/api/stations"),
        new("POST", "/api/stations/{id}/revoke", $"/api/stations/{NotAGuid}/revoke"),
        new("GET", "/api/workflows", "/api/workflows"),
        new("POST", "/api/workflows", "/api/workflows"),
        new("GET", "/api/workflows/actions", "/api/workflows/actions"),
        new("GET", "/api/workflows/variables", "/api/workflows/variables"),
        new("POST", "/api/workflows/validate", "/api/workflows/validate"),
        new("POST", "/api/workflows/dry-run", "/api/workflows/dry-run"),
        new("POST", "/api/email-templates/{id}/preview", $"/api/email-templates/{NotASlug}/preview"),
        new("GET", "/api/workflows/{id}/debug", $"/api/workflows/{NotAGuid}/debug"),
        new("POST", "/api/workflows/{id}/run", $"/api/workflows/{NotAGuid}/run"),
        new("GET", "/api/workflows/health", "/api/workflows/health"),
        new("PUT", "/api/workflows/{id}", $"/api/workflows/{NotAGuid}"),
        new("PUT", "/api/workflows/{id}/enabled", $"/api/workflows/{NotAGuid}/enabled"),
        new("DELETE", "/api/workflows/{id}", $"/api/workflows/{NotAGuid}"),
        new("POST", "/api/content-types/{name}/fields/{field}/change", "/api/content-types/no-such-type/fields/no-such-field/change"),
        new("GET", "/api/content-types/field-changes/{id}", $"/api/content-types/field-changes/{NotAGuid}"),
        new("POST", "/api/content-types/field-changes/{id}/resume", $"/api/content-types/field-changes/{NotAGuid}/resume"),
        new("PUT", "/api/content-types/{name}/numbering", "/api/content-types/no-such-type/numbering"),
        new("PUT", "/api/blocks/schema", "/api/blocks/schema"),
        new("PUT", "/api/blocks/presets/{id}", $"/api/blocks/presets/{NotAGuid}"),
        new("DELETE", "/api/blocks/presets/{id}", $"/api/blocks/presets/{NotAGuid}"),
        new("PUT", "/api/blocks/bindings/{id}", $"/api/blocks/bindings/{NotAGuid}"),
        new("DELETE", "/api/blocks/bindings/{id}", $"/api/blocks/bindings/{NotAGuid}"),
        new("DELETE", "/api/queries/{slug}/erase", $"/api/queries/{NotASlug}/erase"),
        new("POST", "/api/queries/{slug}/restore", $"/api/queries/{NotASlug}/restore"),
        new("DELETE", "/api/roles/{id}/erase", $"/api/roles/{NotAGuid}/erase"),
        new("POST", "/api/roles/{id}/restore", $"/api/roles/{NotAGuid}/restore"),
        new("GET", "/api/roles/deleted", "/api/roles/deleted"),
        new("DELETE", "/api/user-groups/{id}/erase", $"/api/user-groups/{NotAGuid}/erase"),
        new("POST", "/api/user-groups/{id}/restore", $"/api/user-groups/{NotAGuid}/restore"),
        new("GET", "/api/user-groups/deleted", "/api/user-groups/deleted"),
        new("GET", "/api/tenant-settings/email", "/api/tenant-settings/email"),
        new("PUT", "/api/tenant-settings/email", "/api/tenant-settings/email"),
        new("GET", "/api/api-keys/{id}/usage", $"/api/api-keys/{NotAGuid}/usage"),
        new("POST", "/api/api-keys/{id}/rotate", $"/api/api-keys/{NotAGuid}/rotate"),
        new("POST", "/api/viewer-tokens", "/api/viewer-tokens"),
        new("GET", "/api/accounts", "/api/accounts"),
        new("POST", "/api/accounts", "/api/accounts"),
        new("GET", "/api/accounts/{slug}", "/api/accounts/default"),
        new("PUT", "/api/accounts/{slug}", "/api/accounts/default"),
        new("GET", "/api/accounts/{slug}/managers", "/api/accounts/default/managers"),
        new("POST", "/api/accounts/{slug}/managers", "/api/accounts/default/managers"),
        new("DELETE", "/api/accounts/{slug}/managers/{userId}", $"/api/accounts/default/managers/{NotAGuid}"),
        new("POST", "/api/overview/items/{id}/resolve", $"/api/overview/items/{NotAGuid}/resolve"),
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
    /// behavioural theories, one in this class and one each in <see cref="RoleGateWrongRoleTests"/> and
    /// <see cref="RoleGateAdminTests"/> so the collections can run them side by side. So a gated endpoint with no refusal coverage cannot exist: it is
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

    internal static HttpRequestMessage Request(GatedRoute route)
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

    /// <summary>
    /// Roles are resolved from the stored <see cref="barakoCMS.Models.User"/> and its memberships,
    /// not from the token alone, so a caller that has to pass anything beyond the FastEndpoints
    /// gate needs a real user document holding real role ids.
    /// </summary>
    internal static async Task<HttpClient> AuthedAsStoredUser(IntegrationTestFixture factory, params string[] roleNames)
    {
        using var scope = factory.Services.CreateScope();
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

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.CreateToken(roles: roleNames, userId: userId.ToString()));
        return client;
    }

    /// <summary>
    /// A capability gate's legacy role list that names Admin also names SuperAdmin.
    /// </summary>
    /// <remarks>
    /// <c>POST /api/content-types</c> was <c>Roles("Admin")</c> alone, the only gate in the tree
    /// that left SuperAdmin out, from the endpoint's first commit until #448. Nothing caught it:
    /// the seeded admin holds both roles and <see cref="AdminClient"/> authenticates as both, so no
    /// test in the suite has ever presented a SuperAdmin-only principal to a gate.
    ///
    /// Since #443 no core route gates on a role name, so the list read here is the legacy list
    /// each capability gate carries for <c>Auth:LegacyRoleFallback</c>.
    ///
    /// Asserted structurally rather than by adding such a principal to the three theories above.
    /// Those probe with unparseable ids and read status codes, so they would have to know each
    /// route's expected answer; this reads the declared names and needs no request at all.
    ///
    /// If a gate ever should exclude SuperAdmin deliberately, this test is the place to say so,
    /// with the route named and the reason written down. Do not delete it to make one route pass.
    /// </remarks>
    [Fact]
    public void A_legacy_list_that_names_Admin_also_names_SuperAdmin()
    {
        var offenders = LegacyLists()
            .Where(x => x.Legacy.Contains("Admin") && !x.Legacy.Contains("SuperAdmin"))
            .Select(x => x.Route)
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "a legacy list naming Admin but not SuperAdmin locks the higher role out of a surface the "
          + "lower one reaches under Auth:LegacyRoleFallback, and PermissionResolver treats SuperAdmin "
          + "as a blanket bypass everywhere else");
    }

    /// <summary>
    /// The control for the test above: it is reading real gates, not an empty sequence.
    /// </summary>
    [Fact]
    public void Some_legacy_list_actually_names_Admin()
    {
        var naming = LegacyLists().Count(x => x.Legacy.Contains("Admin"));

        naming.Should().BeGreaterThan(0,
            "otherwise the assertion above passes by finding nothing to check, which is how a "
          + "structural test stops testing without failing");
    }

    private IEnumerable<(string Route, IReadOnlyList<string> Legacy)> LegacyLists()
    {
        var core = typeof(Program).Assembly;

        return _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => new
            {
                endpoint.RoutePattern,
                Definition = endpoint.Metadata.OfType<EndpointDefinition>().FirstOrDefault(),
                Required = endpoint.Metadata.GetMetadata<barakoCMS.Infrastructure.Auth.RequiredCapability>(),
            })
            .Where(x => x.Definition is not null && x.Definition.EndpointType.Assembly == core && x.Required is not null)
            .Select(x => ($"/{x.RoutePattern.RawText?.TrimStart('/')}", x.Required!.LegacyRoles));
    }

    /// <summary>
    /// No core route gates on a role name. Issue #443 migrated every one to a capability, and this
    /// pins that at zero so a new endpoint reaching for <c>Roles(...)</c> fails the suite.
    /// </summary>
    /// <remarks>
    /// A route carrying both a capability and a role list counts as on a role name. FastEndpoints
    /// enforces the role list on its own, so a runtime role holding the capability would still be
    /// refused, and excluding such a route here would hide exactly that.
    ///
    /// The fix for a failure is a capability, with the old names as its legacy list, not a line
    /// here.
    /// </remarks>
    [Fact]
    public void No_core_route_gates_on_a_role_name()
    {
        var core = typeof(Program).Assembly;

        var onRoleNames = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => new
            {
                endpoint.RoutePattern,
                Definition = endpoint.Metadata.OfType<EndpointDefinition>().FirstOrDefault(),
                Methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [],
            })
            .Where(x => x.Definition is not null && x.Definition.EndpointType.Assembly == core)
            .Where(x => x.Definition!.AllowedRoles?.Count > 0)
            .SelectMany(x => x.Methods
                .Where(method => x.Definition!.AnonymousVerbs?.Contains(method) != true)
                .Select(method => $"{method} /{x.RoutePattern.RawText?.TrimStart('/')}"))
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        onRoleNames.Should().BeEmpty();
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
