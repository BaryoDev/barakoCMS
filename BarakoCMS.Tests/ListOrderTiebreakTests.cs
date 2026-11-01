using System.Net;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using BarakoCMS.Tests.Builders;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Entries created in the same instant sort equal on CreatedAt, so a page boundary between them is
/// left to the database. Every list that pages on CreatedAt breaks the tie on Id, so the same entry
/// is never shown twice or skipped.
/// </summary>
/// <remarks>
/// The entries share one CreatedAt and are stored with random ids, so the order they went in is not
/// the order of their ids. Eight entries make a chance match between the two orders negligible.
/// </remarks>
[Collection("Integration1")]
public class ListOrderTiebreakTests
{
    private const int Count = 8;
    private static readonly DateTime Moment = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ListOrderTiebreakTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Type, string Marker, List<Guid> Ids)> StoreAsync()
    {
        var type = "tiebreak" + Guid.NewGuid().ToString("n")[..10];
        var marker = "tb" + Guid.NewGuid().ToString("n")[..10];
        var ids = Enumerable.Range(0, Count).Select(_ => Guid.NewGuid()).ToList();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeBuilder().Named(type).PubliclyDeliverable().WithTitleAndSlug().Build());
        for (var i = 0; i < Count; i++)
        {
            session.Store(new ContentBuilder()
                .OfType(type)
                .WithId(ids[i])
                .WithTitleAndSlug($"Entry {i}", $"{marker}-{i}")
                .CreatedAt(Moment)
                .Published()
                .Build());
        }
        await session.SaveChangesAsync(Ct);
        return (type, marker, ids);
    }

    private static List<Guid> InIdOrder(List<Guid> ids) => ids.OrderBy(i => i).ToList();

    [Fact]
    public async Task The_feed_breaks_a_created_at_tie_on_id()
    {
        var (type, _, ids) = await StoreAsync();

        var res = await _client.GetAsync($"/api/public/{type}/feed.xml", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var xml = await res.Content.ReadAsStringAsync(Ct);

        ids.Should().OnlyContain(id => xml.Contains(id.ToString()));
        var shown = ids.OrderBy(id => xml.IndexOf(id.ToString(), StringComparison.Ordinal)).ToList();
        shown.Should().Equal(InIdOrder(ids));
    }

    [Fact]
    public async Task The_sitemap_lists_entries_in_id_order()
    {
        var (_, marker, ids) = await StoreAsync();

        var res = await _client.GetAsync("/api/public/sitemap.xml", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var xml = await res.Content.ReadAsStringAsync(Ct);

        var slugs = Enumerable.Range(0, Count).Select(i => $"{marker}-{i}").ToList();
        slugs.Should().OnlyContain(s => xml.Contains(s));
        var shown = Enumerable.Range(0, Count)
            .OrderBy(i => xml.IndexOf(slugs[i], StringComparison.Ordinal))
            .Select(i => ids[i])
            .ToList();
        shown.Should().Equal(InIdOrder(ids));
    }

    [Fact]
    public async Task The_public_list_breaks_a_created_at_tie_on_id()
    {
        var (type, _, ids) = await StoreAsync();

        var res = await _client.GetAsync($"/api/public/{type}?pageSize=100", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var shown = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();
        shown.Should().HaveCount(Count);
        shown.Should().Equal(InIdOrder(ids));
    }

    [Fact]
    public async Task The_admin_list_breaks_a_created_at_tie_on_id()
    {
        var (type, _, ids) = await StoreAsync();
        var userId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin", Ct);
            if (role is null)
            {
                role = new Role { Id = Guid.NewGuid(), Name = "SuperAdmin" };
                session.Store(role);
            }
            session.Store(new User
            {
                Id = userId,
                Username = $"tiebreak-{userId:n}",
                Email = $"tiebreak-{userId:n}@example.com",
                RoleIds = [role.Id],
            });
            await session.SaveChangesAsync(Ct);
        }

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/contents?contentType={type}&pageSize=50&sortOrder=asc");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["Admin", "SuperAdmin"], userId: userId.ToString()));
        var res = await _client.SendAsync(request, Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var shown = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();
        shown.Should().HaveCount(Count);
        shown.Should().Equal(InIdOrder(ids));
    }
}
