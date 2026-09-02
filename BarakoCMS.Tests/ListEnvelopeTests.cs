using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Every collection endpoint returns the same envelope.
/// </summary>
/// <remarks>
/// Collections came back three ways: the paginated envelope, a bare array, and an ad-hoc wrapper
/// such as <c>{settings: [...]}</c> or <c>{versions: [...]}</c>, with no rule a consumer could
/// learn. A bare array is the one shape that cannot be evolved: adding pagination later changes the
/// root JSON from <c>[</c> to <c>{</c>, which breaks every client with no additive path, so it had
/// to happen in a major or never.
///
/// The assertion is on the JSON root rather than on a deserialised type. Deserialising into
/// <c>PaginatedResponse&lt;T&gt;</c> would pass on a bare array in some serialiser configurations,
/// and the root token is the thing that is actually frozen.
/// </remarks>
[Collection("Sequential")]
public class ListEnvelopeTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ListEnvelopeTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public static TheoryData<string> CollectionEndpoints() =>
    [
        "/api/schemas",
        "/api/user-groups",
        "/api/tenants",
        "/api/api-keys",
        "/api/workflows",
        "/api/me/tenants",
        "/api/tenants/members",
        "/api/tenants/members/roles",
        "/api/settings",
        "/api/accounting/accounts",
        "/api/pwa/installs",
    ];

    [Theory]
    [MemberData(nameof(CollectionEndpoints))]
    public async Task A_collection_endpoint_returns_the_paginated_envelope(string url)
    {
        await AuthenticateAsync("SuperAdmin", "Admin", "Accountant");

        var response = await _client.GetAsync(url);
        response.IsSuccessStatusCode.Should().BeTrue("{0} returned {1}", url, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Object,
            "{0} must not return a bare array: the root type is frozen for the life of 4.x, and an "
            + "array can never gain pagination compatibly", url);

        foreach (var member in new[] { "items", "page", "pageSize", "totalItems", "totalPages" })
        {
            document.RootElement.TryGetProperty(member, out _)
                .Should().BeTrue("{0} is missing '{1}' from the envelope", url, member);
        }

        document.RootElement.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
    }

    /// <summary>
    /// A newly paginated collection returns everything a small deployment had before.
    /// </summary>
    /// <remarks>
    /// These endpoints were unbounded, so any default page size below the maximum would silently
    /// truncate an existing caller. The default is the cap, which is why <c>ListRequest</c> exists
    /// rather than reusing <c>PaginatedRequest</c> and its default of 20.
    /// </remarks>
    [Fact]
    public async Task A_newly_paginated_collection_defaults_to_the_largest_page()
    {
        await AuthenticateAsync("SuperAdmin", "Admin");

        var response = await _client.GetAsync("/api/schemas");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("pageSize").GetInt32()
            .Should().Be(barakoCMS.Models.PaginatedRequest.MaxPageSize);
    }

    /// <summary>
    /// The envelope pages: asking for page 2 at a size of 1 gives a different item and the same total.
    /// </summary>
    /// <remarks>
    /// The positive control. Asserting only on the shape would pass on an endpoint that returns the
    /// envelope and ignores the page parameters, which is the failure mode <c>sortBy</c> already has
    /// elsewhere in this API.
    /// </remarks>
    [Fact]
    public async Task The_envelope_actually_pages()
    {
        await AuthenticateAsync("SuperAdmin", "Admin");
        await SeedContentTypesAsync(3);

        var first = await ReadPageAsync("/api/schemas?page=1&pageSize=1");
        var second = await ReadPageAsync("/api/schemas?page=2&pageSize=1");

        first.Items.Should().HaveCount(1);
        second.Items.Should().HaveCount(1);
        second.Items[0].Should().NotBe(first.Items[0], "page 2 must not repeat page 1");
        second.TotalItems.Should().Be(first.TotalItems);
        first.TotalItems.Should().BeGreaterThanOrEqualTo(3);
    }

    private async Task<(List<string> Items, int TotalItems)> ReadPageAsync(string url)
    {
        var response = await _client.GetAsync(url);
        response.IsSuccessStatusCode.Should().BeTrue("{0} returned {1}", url, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("name").GetString() ?? "")
            .ToList();

        return (items, document.RootElement.GetProperty("totalItems").GetInt32());
    }

    private async Task SeedContentTypesAsync(int count)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        for (var i = 0; i < count; i++)
        {
            session.Store(new barakoCMS.Models.ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = $"envelope-probe-{Guid.NewGuid():n}",
                DisplayName = "Envelope Probe",
                Fields = [],
            });
        }

        await session.SaveChangesAsync();
    }

    private async Task AuthenticateAsync(params string[] roles)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var roleName in roles)
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
            Username = $"envelope-{userId:n}",
            Email = $"envelope-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync();

        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roles, userId: userId.ToString()));
    }
}
