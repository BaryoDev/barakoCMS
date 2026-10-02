using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using barakoCMS.Features.Monitoring.Meta;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// One contract version per core surface, from issue #902: the admin number a console checks and
/// the delivery number a site can check, each reported on its own. A module's number is in the
/// describe document, and <see cref="MetaDescribeTests"/> covers it there.
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

    private readonly IntegrationTestFixture _factory;

    public ContractSurfaceVersionTests(IntegrationTestFixture factory) => _factory = factory;

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
            "a site reads the delivery number from whatever answer it gets first, as a console reads the admin one");
        delivery!.Should().ContainSingle().Which.Should().Be("6");
    }

    [Fact]
    public async Task Meta_keeps_the_admin_field_and_adds_the_delivery_one()
    {
        using var meta = await MetaAsync(await CallerHolding());

        var admin = meta.RootElement.GetProperty("apiContractVersion");
        admin.ValueKind.Should().Be(JsonValueKind.Number, "a released console reads it as a number");
        admin.GetInt32().Should().Be(6);

        var delivery = meta.RootElement.GetProperty("deliveryContractVersion");
        delivery.ValueKind.Should().Be(JsonValueKind.Number);
        delivery.GetInt32().Should().Be(6);

        meta.RootElement.GetProperty("version").ValueKind.Should().Be(JsonValueKind.String);
        meta.RootElement.GetProperty("swaggerEnabled").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
    }

    /// <summary>
    /// <c>/api/meta</c> is not sent no-store, so it may not differ by caller. What depends on a
    /// capability belongs to the describe document beside it.
    /// </summary>
    [Fact]
    public async Task Meta_is_the_same_for_a_caller_who_may_list_modules_and_one_who_may_not()
    {
        using var plain = await MetaAsync(await CallerHolding());
        using var reader = await MetaAsync(await CallerHolding(barakoCMS.Models.SystemCapabilities.ViewModules));

        string[] expected = ["version", "apiContractVersion", "deliveryContractVersion", "swaggerEnabled"];
        plain.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(expected);
        reader.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(expected);
        reader.RootElement.GetRawText().Should().Be(plain.RootElement.GetRawText());
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
    private async Task<HttpClient> CallerHolding(params string[] capabilities)
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

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [roleName], userId: userId.ToString()));
        return client;
    }
}
