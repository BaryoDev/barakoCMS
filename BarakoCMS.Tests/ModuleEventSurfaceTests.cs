using System.Reflection;
using FastEndpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The rule <see cref="EventSurfaceTests"/> holds (DECISIONS.md D4: no API response carries an event
/// type or an event payload), applied to every assembly the running host serves rather than to the
/// core alone.
///
/// <para>
/// The core-side guard reads types out of the core assembly, so the module projects, which put their
/// endpoints under their own roots in their own assemblies, sat outside it. A guard covering part of
/// the surface reads as covering all of it, and the next person adding a module endpoint had nothing
/// telling them the rule exists.
/// </para>
///
/// <para>
/// Endpoints come from the live routing table rather than from reflection over a list of assemblies.
/// That sees whatever the host loaded, needs no module project to grant <c>InternalsVisibleTo</c>,
/// and describes what shipped rather than what happens to be compiled in.
/// <see cref="RoleGateTests"/> reads the same table for the role gates.
/// </para>
///
/// The walk over each response type is <see cref="EventSurfaceTests.Reachable"/>, unchanged, so
/// widening the surface cannot quietly get a weaker check than the core already has.
/// </summary>
[Collection("Sequential")]
public class ModuleEventSurfaceTests
{
    private readonly IntegrationTestFixture _factory;

    public ModuleEventSurfaceTests(IntegrationTestFixture factory)
    {
        _factory = factory;
    }

    private static readonly Assembly Core = typeof(barakoCMS.Modules.IBarakoModule).Assembly;

    private sealed record Surface(Assembly[] Assemblies, Type[] Responses);

    /// <summary>
    /// Every assembly serving an endpoint here, and every response type those endpoints declare.
    /// </summary>
    /// <remarks>
    /// FastEndpoints puts an <see cref="EndpointDefinition"/> on each route's metadata and it carries
    /// the response DTO. <c>ResDtoType</c> is <see cref="object"/> for an endpoint that returns
    /// nothing, and the base-type walk is the fallback for anything FastEndpoints left unfilled; it
    /// is the same one the core guard uses.
    /// </remarks>
    private Surface FromTheRunningHost()
    {
        var definitions = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.Metadata.OfType<EndpointDefinition>().FirstOrDefault())
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .ToArray();

        var assemblies = definitions
            .Select(definition => definition.EndpointType.Assembly)
            .Distinct()
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
            .ToArray();

        var responses = definitions
            .Select(definition => definition.ResDtoType is { } response && response != typeof(object)
                ? response
                : EventSurfaceTests.DeclaredResponseType(definition.EndpointType))
            .Where(response => response is not null)
            .Select(response => response!)
            .Distinct()
            .OrderBy(response => response.FullName, StringComparer.Ordinal)
            .ToArray();

        return new Surface(assemblies, responses);
    }

    [Fact]
    public void No_response_type_the_host_serves_reaches_an_event_type()
    {
        var surface = FromTheRunningHost();

        var walk = EventSurfaceTests.Reachable(surface.Responses);

        walk.Leaks.Should().BeEmpty(
            "an event type on a response freezes that record's shape as public API, and the point of "
          + "keeping the stream internal is that upcasters can reshape it. That holds for a module "
          + "endpoint exactly as it does for a core one. Project to a DTO the endpoint owns instead: "
          + "Features/Content/History is the worked example, mapping events onto VersionResponse");
    }

    /// <summary>
    /// The control. The assertion above passes on an empty set, so a discovery path that stops
    /// finding modules would report the rule as held over a smaller surface, every time, quietly.
    /// The floors below are what turns that into a failure.
    /// </summary>
    [Fact]
    public void The_guard_examines_every_assembly_the_host_serves()
    {
        var surface = FromTheRunningHost();
        var walk = EventSurfaceTests.Reachable(surface.Responses);

        surface.Assemblies.Should().Contain(Core,
            "the core serves most of the API, so losing it means the routing table was read wrong "
          + "rather than that the modules are now covered");

        var modules = surface.Assemblies.Where(assembly => assembly != Core).ToArray();

        modules.Should().HaveCountGreaterThanOrEqualTo(12,
            "the fixture registers twelve module assemblies and every one of them serves endpoints, "
          + "so a smaller number means a module stopped being discovered rather than that the API "
          + "shrank. Removing a module deliberately is the one case where this number moves down");

        surface.Responses.Should().HaveCountGreaterThanOrEqualTo(85,
            "the host declares 93 distinct response types across the core and the modules. A small "
          + "number here means the response type stopped being read off the endpoint definition");

        surface.Responses.Should().Contain(typeof(BarakoCMS.Import.Features.BulkCreate.Response),
            "a module response reached through the routing table is the point of this file, and "
          + "Import is one of the three modules that touch barakoCMS.Events at all");

        walk.Examined.Should().HaveCountGreaterThanOrEqualTo(175,
            "the responses are only the entry points. A walk that stops descending still reports no "
          + "leaks, and nothing above would fail");
    }
}
