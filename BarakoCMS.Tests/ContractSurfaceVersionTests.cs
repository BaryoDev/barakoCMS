using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using barakoCMS.Features.Monitoring.Meta;
using barakoCMS.Modules;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// One contract version per surface, from issue #902: the admin number a console checks, the
/// delivery number a renderer checks, and one per module, each reported on its own.
/// </summary>
/// <remarks>
/// Names and numbers are written out as literals. A released console reads the header by its name,
/// so a test that compared against the constants would follow a rename and stay green.
/// </remarks>
[Collection("Sequential")]
public class ContractSurfaceVersionTests
{
    private const string AdminHeader = "X-Api-Contract-Version";
    private const string DeliveryHeader = "X-Delivery-Contract-Version";

    private const string Zulu = "Zulu Surface Module";
    private const string Mike = "Mike Surface Module";
    private const string Alpha = "Alpha Surface Module";

    private readonly IntegrationTestFixture _factory;

    public ContractSurfaceVersionTests(IntegrationTestFixture factory) => _factory = factory;

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _withModules;

    // Out of alphabetical order, one enabled module that states a version, one enabled module that
    // states none, and one the enabled list left off that states a version nothing else uses, so
    // its number showing up anywhere is unmistakable.
    private WebApplicationFactory<Program> HostWithModules()
    {
        lock (Gate)
        {
            return _withModules ??= _factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                    services.AddSingleton(new ModuleCatalogue(
                    [
                        new ModuleCatalogueEntry(Zulu, ModuleContract.Version, Enabled: true) { HttpContractVersion = 3 },
                        new ModuleCatalogueEntry(Mike, 0, Enabled: false) { HttpContractVersion = 9 },
                        new ModuleCatalogueEntry(Alpha, 0, Enabled: true),
                    ]))));
        }
    }

    [Fact]
    public void The_two_core_surfaces_are_pinned_to_their_numbers()
    {
        // Both literals in one place. Moving one constant without the other turns exactly one of
        // these red, which is how a reviewer sees which surface a change claimed to break.
        ApiContract.Version.Should().Be(6, "the admin surface, the number a console compares itself against");
        ApiContract.DeliveryVersion.Should().Be(6, "the delivery surface, which an admin-only change leaves alone");
        ApiContract.HeaderName.Should().Be(AdminHeader, "a released console reads this name");
        ApiContract.DeliveryHeaderName.Should().Be(DeliveryHeader);
    }

    [Theory]
    [InlineData("/health/build", HttpStatusCode.OK)]
    [InlineData("/api/meta", HttpStatusCode.Unauthorized)]
    [InlineData("/api/no-such-route-for-the-contract-headers", HttpStatusCode.NotFound)]
    public async Task Every_response_carries_both_versions_each_on_its_own_header(string path, HttpStatusCode expected)
    {
        var response = await _factory.CreateClient().GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(expected, "the case is about this class of response");

        response.Headers.TryGetValues(AdminHeader, out var admin).Should().BeTrue(
            "the header a released console reads keeps its name");
        admin!.Should().ContainSingle().Which.Should().Be("6");

        response.Headers.TryGetValues(DeliveryHeader, out var delivery).Should().BeTrue(
            "a renderer reads the delivery number from whatever answer it gets first, as a console does");
        delivery!.Should().ContainSingle().Which.Should().Be("6");
    }

    [Fact]
    public async Task Meta_keeps_the_admin_field_and_adds_the_delivery_one()
    {
        var client = await CallerHolding(_factory);

        using var meta = await MetaAsync(client);

        var admin = meta.RootElement.GetProperty("apiContractVersion");
        admin.ValueKind.Should().Be(JsonValueKind.Number, "a released console reads it as a number");
        admin.GetInt32().Should().Be(6);

        var delivery = meta.RootElement.GetProperty("deliveryContractVersion");
        delivery.ValueKind.Should().Be(JsonValueKind.Number);
        delivery.GetInt32().Should().Be(6);

        meta.RootElement.GetProperty("version").ValueKind.Should().Be(JsonValueKind.String);
        meta.RootElement.GetProperty("swaggerEnabled").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
    }

    [Fact]
    public async Task A_caller_who_may_list_modules_gets_a_version_for_each_enabled_module()
    {
        var client = await CallerHolding(HostWithModules(), barakoCMS.Models.SystemCapabilities.ViewModules);

        using var meta = await MetaAsync(client);

        var modules = meta.RootElement.GetProperty("moduleContractVersions").EnumerateArray().ToArray();
        modules.Should().HaveCount(2, "two of the three modules run, and the one left off has no surface to version");
        modules.Select(m => (m.GetProperty("name").GetString(), m.GetProperty("version").GetInt32()))
            .Should().Equal([(Alpha, 0), (Zulu, 3)]);
        foreach (var module in modules)
        {
            module.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["name", "version"]);
        }

        meta.RootElement.GetRawText().Should().NotContain(Mike, "a module that does not run is not named here");
    }

    [Fact]
    public async Task A_caller_who_may_not_list_modules_gets_no_module_versions()
    {
        // The positive control: the same host answers a caller holding view_modules with the list,
        // so its absence below is the gate and not a host with nothing to say.
        var allowed = await CallerHolding(HostWithModules(), barakoCMS.Models.SystemCapabilities.ViewModules);
        using var seen = await MetaAsync(allowed);
        seen.RootElement.GetProperty("moduleContractVersions").GetArrayLength().Should().Be(2);

        var client = await CallerHolding(HostWithModules());

        using var meta = await MetaAsync(client);

        meta.RootElement.TryGetProperty("moduleContractVersions", out _).Should().BeFalse(
            "the names are the enabled module list, which GET /api/modules keeps behind view_modules");
        var body = meta.RootElement.GetRawText();
        body.Should().NotContain(Alpha);
        body.Should().NotContain(Zulu);
        meta.RootElement.GetProperty("deliveryContractVersion").GetInt32().Should().Be(6,
            "the core numbers are for every signed-in caller");
    }

    [Fact]
    public async Task A_host_running_no_modules_answers_an_empty_list_to_a_caller_who_may_see_it()
    {
        var client = await CallerHolding(_factory, barakoCMS.Models.SystemCapabilities.ViewModules);

        using var meta = await MetaAsync(client);

        var modules = meta.RootElement.GetProperty("moduleContractVersions");
        modules.ValueKind.Should().Be(JsonValueKind.Array, "none is an answer, and it differs from not being allowed to ask");
        modules.GetArrayLength().Should().Be(0);
    }

    private static async Task<JsonDocument> MetaAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/meta", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// A signed-in caller whose one stored role holds exactly the capabilities given. The role name
    /// is unique per call: the fixture database is shared and role names are unique.
    /// </summary>
    private async Task<HttpClient> CallerHolding(WebApplicationFactory<Program> host, params string[] capabilities)
    {
        var roleName = $"Surface Reader {Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new barakoCMS.Models.Role
        {
            Id = Guid.NewGuid(),
            Name = roleName,
            SystemCapabilities = capabilities.ToList(),
        };
        session.Store(role);

        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"surface-{userId:n}",
            Email = $"surface-{userId:n}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [roleName], userId: userId.ToString()));
        return client;
    }
}
