using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Which core endpoints still gate on a role name, pinned so the list can only shrink.
/// </summary>
/// <remarks>
/// Issue #443 is migrating these to capabilities, area by area, because a role somebody creates
/// through <c>POST /api/roles</c> cannot open a gate that matches on a name. The areas listed there
/// are done. These are the ones that were not on its list, most of them written after it.
///
/// The point of pinning the set rather than counting it is that this fails in both directions. Add a
/// new role-name gate and it fails, which is the failure that would have caught #185 and #111: both
/// added one while the migration was in progress, and nothing noticed. Migrate one and it fails too,
/// until the line comes out of this list, which is a deliberate prompt to check the capability landed
/// in <c>SystemCapabilities</c> and in Admin's defaults.
///
/// Nothing here is broken. <c>Roles(...)</c> is FastEndpoints' own authorization and works; what it
/// cannot do is admit a role that did not exist when the code was written.
/// </remarks>
[Collection("Sequential")]
public class CoreRoleGateInventoryTests
{
    private readonly IntegrationTestFixture _factory;

    public CoreRoleGateInventoryTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>
    /// Every core route still gated on a role name, and nothing else.
    /// </summary>
    /// <remarks>
    /// Ordered and deduplicated, so a route with several verbs appears once. Shrinking this is the
    /// remaining work on #443.
    /// </remarks>
    private static readonly string[] StillOnRoleNames =
    [
        "/api/connectors",
        "/api/connectors/{slug}",
        "/api/connectors/{slug}/test",
        "/api/contents/{id}/erase",
        "/api/contents/{id}/rollback/{versionId}",
        "/api/monitoring/health",
        "/api/monitoring/k8s",
        "/api/monitoring/metrics",
        "/api/queries",
        "/api/queries/{slug}",
        "/api/queries/{slug}/preview",
        "/api/redirects",
        "/api/redirects/import",
        "/api/redirects/{id}",
        "/api/requests",
        "/api/requests/{slug}",
        "/api/requests/{slug}/dry-run/{contentId}",
        "/api/workflow-runs",
        "/api/workflow-runs/{id}",
        "/api/workflow-runs/{id}/actions/{ordinal}/retry",
        "/api/workflows",
        "/api/workflows/actions",
        "/api/workflows/dry-run",
        "/api/workflows/validate",
        "/api/workflows/variables",
        "/api/workflows/{id}/debug",
    ];

    [Fact]
    public void The_core_routes_still_gated_on_a_role_name_are_the_ones_listed_here()
    {
        var core = typeof(Program).Assembly;

        var onRoleNames = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .Where(endpoint => endpoint.Metadata.OfType<FastEndpoints.EndpointDefinition>()
                .FirstOrDefault()?.EndpointType.Assembly == core)
            .Where(endpoint => endpoint.Metadata.OfType<IAuthorizeData>()
                .Any(data => !string.IsNullOrWhiteSpace(data.Roles)))
            // An endpoint carrying a capability is migrated. The legacy role list on a capability
            // gate is the migration's compatibility shim, stored as metadata rather than as an
            // authorization requirement, so it does not count as gating on a name.
            .Where(endpoint => endpoint.Metadata
                .GetMetadata<barakoCMS.Infrastructure.Auth.RequiredCapability>() is null)
            .Select(endpoint => (endpoint as RouteEndpoint)?.RoutePattern.RawText)
            .Where(route => route is not null)
            .Select(route => "/" + route!.TrimStart('/'))
            .Distinct()
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToList();

        onRoleNames.Should().NotBeEmpty(
            "some core routes are known to still gate on a role name, so reading none means this "
          + "test stopped looking rather than that the migration finished");

        onRoleNames.Should().BeEquivalentTo(StillOnRoleNames,
            "adding a role-name gate is a step backwards on #443 and migrating one is a step "
          + "forwards; either way this list is the record and has to be edited on purpose");
    }
}
