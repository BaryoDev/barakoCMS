using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using barakoCMS.Modules;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #490: the capability vocabulary is discoverable at <c>GET /api/capabilities</c>, and a role
/// write checks its <c>systemCapabilities</c> against it.
/// </summary>
/// <remarks>
/// The fixture's shared host serves every first-party module's endpoints but registers no
/// <see cref="IBarakoModule"/>, so on it a module name is attributed to its assembly. The listing
/// test builds a host that registers the Accounting module the way <c>AddBarakoCMS</c> would, so the
/// source column can be checked against the module's own name.
///
/// <c>Roles:RefuseUnknownCapabilities</c> defaults to off, which is today's behaviour: the role
/// saves. The strict cases run on a derived host that turns it on.
/// </remarks>
[Collection("Sequential")]
public class CapabilityVocabularyTests
{
    private readonly IntegrationTestFixture _factory;

    public CapabilityVocabularyTests(IntegrationTestFixture factory) => _factory = factory;

    private const string Typo = "view_pwa_instals";

    [Fact]
    public async Task The_list_holds_a_core_name_and_a_name_from_a_registered_module()
    {
        var client = await SuperAdminOn(HostWithAccountingRegistered());

        var response = await client.GetAsync("/api/capabilities?pageSize=100", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await ItemsAsync(response);
        items.Should().NotBeEmpty("an empty list would make every assertion below vacuous");

        var core = items.Single(i => i.Name == SystemCapabilities.ManageRoles);
        core.Source.Should().Be(CapabilityVocabulary.CoreSource);

        var module = items.Single(i => i.Name == BarakoCMS.Accounting.AccountingCapabilities.ViewLedger);
        module.Source.Should().Be(new BarakoCMS.Accounting.AccountingModule().Name,
            "a name a registered module's endpoint asks for is attributed to that module");

        var wildcard = items.Single(i => i.Name == SystemCapabilities.All);
        wildcard.Note.Should().NotBeNullOrWhiteSpace("the wildcard is the one name that needs explaining");
        items.Where(i => i.Name != SystemCapabilities.All).Should().OnlyContain(i => i.Note == null);
    }

    /// <summary>
    /// The list is what the gates enforce: every capability any served endpoint asks for is in it.
    /// </summary>
    [Fact]
    public async Task Every_capability_a_served_endpoint_asks_for_is_listed()
    {
        var client = await SuperAdminOn(_factory);

        var response = await client.GetAsync("/api/capabilities?pageSize=100", TestContext.Current.CancellationToken);
        var listed = (await ItemsAsync(response)).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var required = _factory.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .Select(endpoint => endpoint.Metadata.GetMetadata<RequiredCapability>()?.Capability)
            .Where(capability => capability is not null)
            .Distinct()
            .ToList();

        required.Should().NotBeEmpty("the host serves gated endpoints, so reading none means this stopped looking");
        required.Should().OnlyContain(capability => listed.Contains(capability!));
        listed.Should().Contain(BarakoCMS.Pwa.PwaCapabilities.ViewPwaInstalls,
            "a module capability reaches the list from the routing table without the module registering itself");
    }

    [Fact]
    public async Task A_caller_without_manage_roles_is_refused()
    {
        var client = await CallerHolding(SystemCapabilities.ViewAuditLog);

        var response = await client.GetAsync("/api/capabilities", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var response = await _factory.CreateClient().GetAsync("/api/capabilities", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_typo_on_create_is_returned_and_the_role_still_saves()
    {
        var client = await SuperAdminOn(_factory);
        var name = $"Typo Role {Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/roles",
            RoleBody(name, [Typo, SystemCapabilities.ViewAuditLog]), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("unknownCapabilities").EnumerateArray().Select(e => e.GetString())
            .Should().Equal([Typo], "the known name is not reported, only the typo");

        var stored = await LoadByNameAsync(name);
        stored.Should().NotBeNull("the default is to save and warn, which is what happened before this check existed");
        stored!.SystemCapabilities.Should().BeEquivalentTo([Typo, SystemCapabilities.ViewAuditLog],
            "nothing an operator wrote is discarded");
    }

    [Fact]
    public async Task A_typo_on_update_is_returned_and_the_role_still_saves()
    {
        var client = await SuperAdminOn(_factory);
        var name = $"Typo Update Role {Guid.NewGuid():N}";
        var id = await CreateAsync(client, name, [SystemCapabilities.ViewAuditLog]);

        var response = await client.PutAsJsonAsync($"/api/roles/{id}",
            RoleBody(name, [Typo], id), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("unknownCapabilities").EnumerateArray().Select(e => e.GetString())
            .Should().Equal([Typo]);

        (await LoadByNameAsync(name))!.SystemCapabilities.Should().Equal([Typo]);
    }

    [Fact]
    public async Task With_refusal_on_a_typo_on_create_is_a_400_naming_it_and_nothing_is_saved()
    {
        var client = await SuperAdminOn(StrictHost());
        var name = $"Refused Role {Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/roles",
            RoleBody(name, [Typo, SystemCapabilities.ViewAuditLog]), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var reasons = await ProblemDetailReasonsAsync(response);
        reasons.Should().ContainSingle(r => r.Contains(Typo) && r.Contains("/api/capabilities"),
            "the message names the unknown name and points at where the vocabulary is listed");
        reasons.Should().NotContain(r => r.Contains(SystemCapabilities.ViewAuditLog));

        (await LoadByNameAsync(name)).Should().BeNull("a refused write saves nothing");
    }

    [Fact]
    public async Task With_refusal_on_a_typo_on_update_is_a_400_and_the_role_is_unchanged()
    {
        var strict = await SuperAdminOn(StrictHost());
        var name = $"Refused Update Role {Guid.NewGuid():N}";
        var id = await CreateAsync(strict, name, [SystemCapabilities.ViewAuditLog]);

        var response = await strict.PutAsJsonAsync($"/api/roles/{id}",
            RoleBody(name, [Typo], id), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemDetailReasonsAsync(response)).Should().ContainSingle(r => r.Contains(Typo));

        (await LoadByNameAsync(name))!.SystemCapabilities.Should().Equal([SystemCapabilities.ViewAuditLog]);
    }

    /// <summary>
    /// The wildcard is not an endpoint's name, so it has to be known on its own, both ways.
    /// </summary>
    [Fact]
    public async Task The_wildcard_is_known_whether_or_not_unknown_names_are_refused()
    {
        foreach (var host in new[] { _factory, StrictHost() })
        {
            var client = await SuperAdminOn(host);
            var name = $"Wildcard Role {Guid.NewGuid():N}";

            var response = await client.PostAsJsonAsync("/api/roles",
                RoleBody(name, [SystemCapabilities.All]), TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            body.GetProperty("unknownCapabilities").GetArrayLength().Should().Be(0);
        }
    }

    /// <summary>
    /// A name is compared the way <see cref="SystemCapabilities.Satisfies"/> compares it, so a
    /// differently cased spelling of a real name is not reported as a typo.
    /// </summary>
    [Fact]
    public async Task A_known_name_in_another_case_is_not_unknown()
    {
        var client = await SuperAdminOn(StrictHost());
        var name = $"Cased Role {Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/api/roles",
            RoleBody(name, ["MANAGE_ROLES"]), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static object RoleBody(string name, string[] capabilities, Guid? id = null) => new
    {
        id,
        name,
        description = "",
        permissions = Array.Empty<object>(),
        systemCapabilities = capabilities,
    };

    private static async Task<Guid> CreateAsync(HttpClient client, string name, string[] capabilities)
    {
        var response = await client.PostAsJsonAsync("/api/roles", RoleBody(name, capabilities), TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.GetProperty("id").GetGuid();
    }

    private async Task<Role?> LoadByNameAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Role>().FirstOrDefaultAsync(r => r.Name == name, TestContext.Current.CancellationToken);
    }

    private sealed record Listed(string Name, string Source, string? Note);

    private static async Task<IReadOnlyList<Listed>> ItemsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => new Listed(
                i.GetProperty("name").GetString()!,
                i.GetProperty("source").GetString()!,
                i.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String ? note.GetString() : null))
            .ToList();
    }

    private static async Task<List<string>> ProblemDetailReasonsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.TryGetProperty("errors", out var errors)
            .Should().BeTrue("ProblemDetails carries its entries under 'errors'");

        return errors.EnumerateArray()
            .Select(entry => entry.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "" : "")
            .Where(reason => reason.Length > 0)
            .ToList();
    }

    private async Task<HttpClient> SuperAdminOn(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    /// <summary>
    /// A caller whose one role holds exactly these capabilities, under a name no gate lists, so the
    /// legacy fallback cannot be what admits it.
    /// </summary>
    private async Task<HttpClient> CallerHolding(params string[] capabilities)
    {
        var unique = $"Vocabulary Caller {Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role { Id = Guid.NewGuid(), Name = unique, SystemCapabilities = capabilities.ToList() };
        session.Store(role);

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"vocab-{userId:n}",
            Email = $"vocab-{userId:n}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [unique], userId: userId.ToString()));
        return client;
    }

    // One derived host of each kind for the whole class. Each WithWebHostBuilder call builds another
    // server against the shared database.
    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _strict;
    private static WebApplicationFactory<Program>? _withAccounting;

    private WebApplicationFactory<Program> StrictHost()
    {
        lock (Gate)
        {
            return _strict ??= _factory.WithSetting(CapabilityVocabulary.RefuseUnknownKey, "true");
        }
    }

    /// <summary>
    /// The shared host already runs the Accounting endpoints; this one also registers the module
    /// instance, which is the half <c>AddBarakoCMS</c> does and the fixture does not.
    /// </summary>
    private WebApplicationFactory<Program> HostWithAccountingRegistered()
    {
        lock (Gate)
        {
            return _withAccounting ??= _factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                    services.AddSingleton<IBarakoModule>(new BarakoCMS.Accounting.AccountingModule())));
        }
    }
}
