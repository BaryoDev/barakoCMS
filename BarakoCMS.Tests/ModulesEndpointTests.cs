using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using barakoCMS.Modules;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// GET /api/modules, from issue #185: which modules this instance saw, whether each runs (#170),
/// and nothing else.
/// </summary>
/// <remarks>
/// The fixture's own host configures module services directly, turns discovery off, and so gets an
/// empty <see cref="ModuleCatalogue"/> from <c>AddBarakoCMS</c>, which is what the endpoint reads.
/// So the listing tests build a host that replaces the catalogue with three entries, and the shared
/// host stands in for a deployment running no modules at all.
/// </remarks>
[Collection("Sequential")]
public class ModulesEndpointTests
{
    private readonly IntegrationTestFixture _factory;

    public ModulesEndpointTests(IntegrationTestFixture factory) => _factory = factory;

    // Recorded out of alphabetical order, with one module declaring a contract version and one
    // leaving it at the unstated default, because both are cases the contract accepts, and one
    // that the enabled list left off, which is the case the catalogue exists to report.
    private const string Zulu = "Zulu Probe Module";
    private const string Alpha = "Alpha Probe Module";
    private const string Mike = "Mike Probe Module";

    // One derived host for the whole class. Each WithWebHostBuilder call builds another server
    // against the shared database, and this needs exactly one.
    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _withModules;

    private WebApplicationFactory<Program> HostWithModules()
    {
        lock (Gate)
        {
            return _withModules ??= _factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    // A plain AddSingleton after AddBarakoCMS registered the empty one: the last
                    // registration is the one the endpoint resolves.
                    services.AddSingleton(new ModuleCatalogue(
                    [
                        new ModuleCatalogueEntry(Zulu, ModuleContract.Version, Enabled: true),
                        new ModuleCatalogueEntry(Mike, 0, Enabled: false),
                        new ModuleCatalogueEntry(Alpha, 0, Enabled: true),
                    ]));
                }));
        }
    }

    [Fact]
    public async Task It_lists_the_modules_the_host_registered()
    {
        var client = await AdminClientFor(HostWithModules());

        var response = await client.GetAsync("/api/modules", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await ItemsAsync(response);
        items.Should().HaveCount(3, "the host saw three modules, and an empty list would make every assertion below vacuous");
        // Ordered, and the host recorded them in another order: two calls, and two deployments
        // of the same set, have to agree on the order.
        items.Select(i => i.GetProperty("name").GetString()).Should().Equal([Alpha, Mike, Zulu]);
        items[0].GetProperty("contractVersion").GetInt32().Should().Be(0, "a module that states no contract version reports the unstated default");
        items[2].GetProperty("contractVersion").GetInt32().Should().Be(ModuleContract.Version);
    }

    /// <summary>
    /// Issue #170: a module the enabled list left off is still installed, and an operator asking
    /// "is it off, or not there" needs the two told apart. Off is listed with enabled false; not
    /// there is not listed.
    /// </summary>
    [Fact]
    public async Task A_module_the_enabled_list_left_off_is_listed_with_enabled_false()
    {
        var client = await AdminClientFor(HostWithModules());

        var response = await client.GetAsync("/api/modules", TestContext.Current.CancellationToken);

        var items = await ItemsAsync(response);
        items.Should().HaveCount(3);
        items.Select(i => (i.GetProperty("name").GetString(), i.GetProperty("enabled").GetBoolean()))
            .Should().Equal([(Alpha, true), (Mike, false), (Zulu, true)]);
    }

    /// <summary>
    /// "None" is an answer. A 404 here would be indistinguishable from a route that never shipped,
    /// which is the thing a client library asking this question is trying to tell apart.
    /// </summary>
    [Fact]
    public async Task A_host_with_no_registered_modules_answers_with_an_empty_list()
    {
        var client = await AdminClientFor(_factory);

        var response = await client.GetAsync("/api/modules", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ItemsAsync(response)).Should().BeEmpty();
    }

    /// <summary>
    /// The whole security argument for this endpoint. A module knows its configuration section, its
    /// assemblies and therefore its file paths; none of that is a module fact, all of it is
    /// reconnaissance, and the response carries three fields so there is nowhere for it to hide.
    /// </summary>
    [Fact]
    public async Task It_reports_the_name_the_contract_version_and_enabled_and_nothing_else()
    {
        var client = await AdminClientFor(HostWithModules());

        var response = await client.GetAsync("/api/modules", TestContext.Current.CancellationToken);

        var items = await ItemsAsync(response);
        items.Should().NotBeEmpty("with no items there are no property names to check");

        foreach (var item in items)
        {
            item.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["name", "contractVersion", "enabled"]);
        }
    }

    [Fact]
    public async Task It_refuses_an_anonymous_caller()
    {
        var response = await HostWithModules().CreateClient()
            .GetAsync("/api/modules", TestContext.Current.CancellationToken);

        // Exactly 401. "Not 200" would be satisfied by a 404, which is what a route that was never
        // registered looks like, and the listing test above is the paired case that must still pass.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task It_refuses_a_signed_in_caller_who_is_not_an_admin()
    {
        var client = await ClientFor(HostWithModules(), "User");

        var response = await client.GetAsync("/api/modules", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<IReadOnlyList<JsonElement>> ItemsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);

        // Cloned, because the items outlive the JsonDocument this using disposes.
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.Clone())
            .ToArray();
    }

    private Task<HttpClient> AdminClientFor(WebApplicationFactory<Program> host)
        => ClientFor(host, "SuperAdmin", "Admin");

    /// <summary>
    /// A token alone is not enough anywhere else in this suite, so the caller gets a stored user
    /// holding the same roles, exactly as <see cref="RoleGateTests"/> does.
    /// </summary>
    private async Task<HttpClient> ClientFor(WebApplicationFactory<Program> host, params string[] roleNames)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new System.Collections.Generic.List<Guid>();
        foreach (var roleName in roleNames)
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
            Username = $"modules-{userId:n}",
            Email = $"modules-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync();

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roleNames, userId: userId.ToString()));
        return client;
    }
}
