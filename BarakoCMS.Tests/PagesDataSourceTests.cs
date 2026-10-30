using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using BarakoCMS.Pages;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>A data source a test module would register, gated on a capability Admin holds.</summary>
public sealed class ProbeBlockDataSource : IBlockDataSource
{
    public const string SourceId = "probe.sessions";

    public string Id => SourceId;

    public BlockDataSourceShape Shape { get; } = new([new("title", "string"), new("startsAt", "date")]);

    public string? Capability => SystemCapabilities.ManageContentTypes;
}

/// <summary>
/// The block data source list (#1257): <c>GET /api/pages/data-sources</c>, and the rules the
/// registry holds its sources to.
/// </summary>
[Collection("Integration1")]
public class PagesDataSourceTests
{
    private const string Route = "/api/pages/data-sources";

    private readonly IntegrationTestFixture _factory;

    public PagesDataSourceTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> SignedInAsync(params string[] roleNames)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var name in roleNames)
        {
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == name, Ct);
            roleIds.Add(role!.Id);
        }

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"datasources-{userId:n}",
            Email = $"datasources-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync(Ct);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roleNames, userId: userId.ToString()));
        return client;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static string[] Ids(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public async Task The_list_requires_a_signed_in_caller()
    {
        var res = await _factory.CreateClient().GetAsync(Route, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_caller_without_the_capability_sees_only_sources_that_name_none()
    {
        var client = await SignedInAsync();

        var ids = Ids(await ReadAsync(await client.GetAsync(Route, Ct)));

        ids.Should().Contain("entries");
        ids.Should().NotContain(ProbeBlockDataSource.SourceId, "its capability is manage_content_types");
    }

    [Fact]
    public async Task A_caller_holding_the_capability_sees_the_source_with_its_shape()
    {
        var client = await SignedInAsync("SuperAdmin", "Admin");

        var body = await ReadAsync(await client.GetAsync(Route, Ct));

        body.GetProperty("contract").GetInt32().Should().Be(PagesContract.Version);
        var probe = body.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetString() == ProbeBlockDataSource.SourceId);
        probe.GetProperty("capability").GetString().Should().Be(SystemCapabilities.ManageContentTypes);
        probe.GetProperty("shape").GetProperty("fields").EnumerateArray()
            .Select(f => (f.GetProperty("name").GetString(), f.GetProperty("kind").GetString()))
            .Should().Equal(("title", "string"), ("startsAt", "date"));
    }

    [Fact]
    public async Task Core_registers_the_entries_source()
    {
        var client = await SignedInAsync("SuperAdmin", "Admin");

        var ids = Ids(await ReadAsync(await client.GetAsync(Route, Ct)));

        ids.Should().Contain("entries");
    }

    [Fact]
    public async Task The_list_is_ordered_by_id()
    {
        var client = await SignedInAsync("SuperAdmin", "Admin");

        var ids = Ids(await ReadAsync(await client.GetAsync(Route, Ct)));

        ids.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    // The registry

    private sealed class Source(string id, params string[] fields) : IBlockDataSource
    {
        public string Id => id;
        public BlockDataSourceShape Shape { get; } = new([.. fields.Select(f => new BlockDataSourceField(f, "string"))]);
        public string? Capability => null;
    }

    [Fact]
    public void The_registry_finds_a_source_by_id()
    {
        var registry = new BlockDataSources([new Source("b", "x"), new Source("a", "x")]);

        registry.Find("a").Should().NotBeNull();
        registry.Find("missing").Should().BeNull();
        registry.All.Select(s => s.Id).Should().Equal("a", "b");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Forms")]
    [InlineData("1forms")]
    [InlineData("has space")]
    [InlineData("a/b")]
    public void The_registry_refuses_a_malformed_id(string id)
    {
        var act = () => new BlockDataSources([new Source(id, "x")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*is not valid*");
    }

    [Fact]
    public void The_registry_refuses_an_id_longer_than_the_limit()
    {
        var act = () => new BlockDataSources([new Source(new string('a', BlockDataSources.MaxIdLength + 1), "x")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*is not valid*");
    }

    [Fact]
    public void The_registry_refuses_two_sources_with_one_id()
    {
        var act = () => new BlockDataSources([new Source("dup", "x"), new Source("dup", "y")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'dup' is registered by both*");
    }

    [Fact]
    public void The_registry_refuses_a_source_with_no_fields()
    {
        var act = () => new BlockDataSources([new Source("empty")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*declares no fields*");
    }
}
