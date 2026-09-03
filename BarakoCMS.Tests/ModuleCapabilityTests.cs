using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using barakoCMS.Modules;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #443, area 5: the module endpoints gate on capabilities a module declares for itself.
/// </summary>
/// <remarks>
/// Core cannot name a module's capabilities, because core does not reference a module and a
/// third-party one is not in this repository at all. So <c>SystemCapabilities.DefaultsFor</c> cannot
/// carry them, and a module grants its own at seed time to the roles its old <c>Roles(...)</c> gate
/// listed. Without that step a migrated module would be reachable only through the legacy role-name
/// fallback, and turning that off, which is the whole point of the issue, would take every module
/// away from every Admin.
/// </remarks>
[Collection("Sequential")]
public class ModuleCapabilityTests
{
    private readonly IntegrationTestFixture _factory;

    public ModuleCapabilityTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>
    /// A runtime role holding one module capability reaches that module and nothing else.
    /// </summary>
    /// <remarks>
    /// The role's name is a fresh GUID, so the legacy fallback cannot be what admits it: no gate
    /// lists that name. Whatever it reaches, it reaches on the capability.
    /// </remarks>
    [Fact]
    public async Task A_runtime_role_holding_a_module_capability_reaches_that_module()
    {
        var client = await CallerHolding(BarakoCMS.Accounting.AccountingCapabilities.ViewLedger);

        var balances = await client.GetAsync("/api/accounting/balances", TestContext.Current.CancellationToken);
        var post = await client.SendAsync(
            Probe("POST", "/api/accounting/journal-entries"), TestContext.Current.CancellationToken);
        var flags = await client.GetAsync("/api/feature-flags/admin", TestContext.Current.CancellationToken);

        balances.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "view_ledger is what the balances endpoint asks for");
        balances.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "reading the books and writing to them are two grants");
        flags.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "and a capability from one module says nothing about another");
    }

    /// <summary>The other half of the accounting split, so it holds in both directions.</summary>
    [Fact]
    public async Task Post_journal_entries_does_not_carry_the_ledger_with_it()
    {
        var client = await CallerHolding(BarakoCMS.Accounting.AccountingCapabilities.PostEntries);

        var post = await client.SendAsync(
            Probe("POST", "/api/accounting/journal-entries"), TestContext.Current.CancellationToken);
        var balances = await client.GetAsync("/api/accounting/balances", TestContext.Current.CancellationToken);

        post.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        balances.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The export and import split, which is the one where the two risks are opposite.
    /// </summary>
    [Fact]
    public async Task Export_and_import_are_separate_grants()
    {
        var exporter = await CallerHolding(BarakoCMS.Portability.PortabilityCapabilities.ExportContent);

        var export = await exporter.GetAsync("/api/portability/export", TestContext.Current.CancellationToken);
        var import = await exporter.SendAsync(
            Probe("POST", "/api/portability/import"), TestContext.Current.CancellationToken);

        export.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "reading a tenant out is what this role was granted");
        import.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "writing a whole tenant in is the opposite risk and a separate grant");
    }

    /// <summary>
    /// A role holding nothing reaches no module endpoint, so the tests above are not passing because
    /// every module endpoint is open.
    /// </summary>
    [Fact]
    public async Task A_runtime_role_holding_nothing_reaches_no_module()
    {
        var client = await CallerHolding();

        foreach (var route in new[]
        {
            "/api/accounting/balances", "/api/feature-flags/admin", "/api/portability/export",
            "/api/client-errors", "/api/pwa/installs", "/api/email-events", "/api/analytics/websites",
        })
        {
            var response = await client.GetAsync(route, TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "{0} should be refused", route);
        }
    }

    /// <summary>
    /// Structural, the module counterpart of the core inventory test: a capability a module endpoint
    /// asks for must be one that module declares, so a typo cannot ship as a route nobody can reach.
    /// </summary>
    /// <remarks>
    /// Nothing validates a capability name on the way into a role, which is what lets a module
    /// declare its own without core knowing. The same absence means a misspelled name in a gate is
    /// accepted silently and grants nobody anything, and only the legacy fallback would hide it.
    /// </remarks>
    [Fact]
    public void Every_capability_a_module_endpoint_requires_is_one_its_module_declares()
    {
        // Discovered off the assemblies that actually registered endpoints, rather than a list
        // maintained here. A hardcoded list is a gate that only covers the modules somebody
        // remembered to add to it, and it failed exactly that way when the Import module arrived
        // with a capability of its own.
        var declared = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .Select(endpoint => endpoint.Metadata.OfType<FastEndpoints.EndpointDefinition>()
                .FirstOrDefault()?.EndpointType.Assembly)
            .Where(assembly => assembly is not null)
            .Distinct()
            .SelectMany(assembly => LoadableTypes.In(assembly!))
            .Where(t => t.IsAbstract && t.IsSealed && t.Name.EndsWith("Capabilities", StringComparison.Ordinal))
            .SelectMany(t => t.GetFields()
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        declared.Should().NotBeEmpty("the modules declare capability classes, or this stopped looking");

        var core = typeof(Program).Assembly;

        var required = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .Where(endpoint => endpoint.Metadata.OfType<FastEndpoints.EndpointDefinition>()
                .FirstOrDefault()?.EndpointType.Assembly is { } assembly && assembly != core)
            .Select(endpoint => endpoint.Metadata.GetMetadata<barakoCMS.Infrastructure.Auth.RequiredCapability>())
            .Where(metadata => metadata is not null)
            .Select(metadata => metadata!.Capability)
            .Distinct()
            .ToList();

        required.Should().NotBeEmpty(
            "module endpoints declare capability gates, so reading none means this test stopped looking");
        required.Should().OnlyContain(capability => declared.Contains(capability));
    }

    /// <summary>No module endpoint still gates on a role name.</summary>
    /// <remarks>
    /// The "done when" of issue #443 read through the running host rather than through a grep, so a
    /// module added later that gates on a name fails here rather than being noticed by somebody.
    /// The legacy fallback list on a capability gate is not this: that is the migration's
    /// compatibility shim and it is stored as metadata, not as an authorization requirement.
    /// </remarks>
    [Fact]
    public void No_module_endpoint_gates_on_a_role_name_alone()
    {
        var core = typeof(Program).Assembly;

        var ungated = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .Where(endpoint => endpoint.Metadata.OfType<FastEndpoints.EndpointDefinition>()
                .FirstOrDefault()?.EndpointType.Assembly is { } assembly && assembly != core)
            .Where(endpoint => endpoint.Metadata
                .OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                .Any(data => !string.IsNullOrWhiteSpace(data.Roles)))
            .Where(endpoint => endpoint.Metadata
                .GetMetadata<barakoCMS.Infrastructure.Auth.RequiredCapability>() is null)
            .Select(endpoint => (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? endpoint.DisplayName)
            .ToList();

        ungated.Should().BeEmpty(
            "a module endpoint gating on a role name cannot be reached by a role somebody created");
    }

    private static HttpRequestMessage Probe(string verb, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(verb), path);

        // Deliberately not valid JSON, as in RoleGateTests: the gate refuses before a binding failure
        // is answered, so an allowed caller gets a 400 that mutates nothing.
        request.Content = new StringContent("~", System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>
    /// A caller whose one role holds exactly these capabilities, under a name no gate lists, so the
    /// legacy fallback cannot be what admits it.
    /// </summary>
    private async Task<HttpClient> CallerHolding(params string[] capabilities)
    {
        var unique = $"Module Caller {Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role { Id = Guid.NewGuid(), Name = unique, SystemCapabilities = capabilities.ToList() };
        session.Store(role);

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"modcap-{userId:n}",
            Email = $"modcap-{userId:n}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [unique], userId: userId.ToString()));
        return client;
    }
}
