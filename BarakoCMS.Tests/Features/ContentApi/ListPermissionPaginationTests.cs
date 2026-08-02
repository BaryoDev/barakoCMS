using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// H.3: Content/List counts TotalItems and paginates BEFORE permission-filtering, so a user who can
/// only see a subset of a content type (a real, common rule — "see only what you own") gets pages
/// that lie about how much content exists and can come back short or empty even when visible items
/// remain further down the raw (unfiltered) ordering.
///
/// These tests use an ownership condition (`OwnerId _eq $CURRENT_USER`), the same
/// Directus/Strapi-style condition <see cref="barakoCMS.Infrastructure.Services.ConditionEvaluator"/>
/// already supports, because it is exactly the kind of per-item rule that makes a coarse,
/// no-specific-item permission check unsafe to use as a pagination pre-filter.
/// </summary>
[Collection("Sequential")]
public class ListPermissionPaginationTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ListPermissionPaginationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<string> SeedContentType()
    {
        var contentType = $"doc_{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = contentType,
            DisplayName = "Document",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public },
                new() { Name = "OwnerId", Type = "string", Sensitivity = SensitivityLevel.Public },
            },
        });
        await session.SaveChangesAsync();
        return contentType;
    }

    // A user whose Read rule is conditioned on owning the record — every rule in
    // SensitivityIntegrationTests grants unconditionally; this one does not, which is the case
    // Content/List's current pagination gets wrong.
    private async Task<(string Token, Guid UserId)> SetupOwnerScopedReader(string contentType)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();

        var userId = Guid.NewGuid();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"owner_only_{Guid.NewGuid():N}",
            Permissions = new List<ContentTypePermission>
            {
                new()
                {
                    ContentTypeSlug = contentType,
                    Read = new PermissionRule
                    {
                        Enabled = true,
                        Conditions = new Dictionary<string, object>
                        {
                            ["OwnerId"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
                        },
                    },
                    Create = new PermissionRule { Enabled = false },
                    Update = new PermissionRule { Enabled = false },
                    Delete = new PermissionRule { Enabled = false },
                },
            },
        };
        session.Store(role);
        var user = new User
        {
            Id = userId,
            Username = $"user_{Guid.NewGuid()}",
            Email = $"{Guid.NewGuid()}@example.com",
            RoleIds = new List<Guid> { role.Id },
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var token = _factory.CreateToken(new[] { "Viewer" }, user.Id.ToString());
        return (token, userId);
    }

    // Seeds `count` items in strictly increasing CreatedAt order (so ascending sort is deterministic),
    // tagging each item's OwnerId per `ownerFor(index)` so the caller controls exactly which items the
    // scoped reader can see and in what position.
    private async Task SeedDocumentsAsync(string contentType, int count, Func<int, string> ownerFor)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();
        var baseTime = DateTime.UtcNow.AddMinutes(-count);
        for (var i = 0; i < count; i++)
        {
            session.Store(new barakoCMS.Models.Content
            {
                Id = Guid.NewGuid(),
                ContentType = contentType,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { { "Title", $"Doc {i}" }, { "OwnerId", ownerFor(i) } },
                CreatedAt = baseTime.AddSeconds(i),
                UpdatedAt = baseTime.AddSeconds(i),
            });
        }
        await session.SaveChangesAsync();
    }

    private async Task<JsonElement> ListAsync(string token, string contentType, int page, int pageSize)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.GetAsync(
            $"/api/contents?contentType={contentType}&page={page}&pageSize={pageSize}&sortOrder=asc");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static JsonElement Prop(JsonElement root, params string[] names)
    {
        foreach (var n in names)
            if (root.TryGetProperty(n, out var v)) return v;
        throw new Xunit.Sdk.XunitException($"none of [{string.Join(",", names)}] in {root}");
    }

    [Fact]
    public async Task First_page_is_not_empty_when_visible_items_exist_further_down_the_raw_order()
    {
        var ct = await SeedContentType();
        var (token, userId) = await SetupOwnerScopedReader(ct);

        // 10 items, oldest-to-newest: the first 8 belong to someone else (denied), the last 2 belong
        // to this user (visible). A raw (unfiltered) page-1 window of 5, taken oldest-first, is 100%
        // denied items — today's code filters AFTER that window is fixed, so page 1 comes back
        // empty even though 2 visible documents genuinely exist in the set.
        await SeedDocumentsAsync(ct, 10, i => i < 8 ? "someone-else" : userId.ToString());

        var page1 = await ListAsync(token, ct, page: 1, pageSize: 5);
        var items = Prop(page1, "items", "Items");

        items.GetArrayLength().Should().Be(2,
            "there are exactly 2 documents this user can see, and they must appear on the first page " +
            "of results instead of being hidden behind a page whose raw window was all denied items");
    }

    [Fact]
    public async Task TotalItems_reflects_what_the_user_can_actually_see_not_the_raw_row_count()
    {
        var ct = await SeedContentType();
        var (token, userId) = await SetupOwnerScopedReader(ct);

        await SeedDocumentsAsync(ct, 10, i => i < 8 ? "someone-else" : userId.ToString());

        var page1 = await ListAsync(token, ct, page: 1, pageSize: 5);

        Prop(page1, "totalItems", "TotalItems").GetInt32().Should().Be(2,
            "TotalItems must count only the documents this user is permitted to read (2), " +
            "not all 10 rows that merely match the content-type filter");
    }

    [Fact]
    public async Task Paginating_through_every_page_yields_every_visible_item_exactly_once()
    {
        var ct = await SeedContentType();
        var (token, userId) = await SetupOwnerScopedReader(ct);

        // A denser interleaving: every third document is visible, the rest belong to someone else.
        // With pageSize=2 this straddles denied items differently on every page, which is the
        // condition most likely to duplicate or drop a visible item if pagination runs over the
        // unfiltered set instead of the permitted one.
        await SeedDocumentsAsync(ct, 12, i => i % 3 == 0 ? userId.ToString() : "someone-else");
        // Visible indices: 0, 3, 6, 9 -> 4 visible documents total.

        var seenTitles = new HashSet<string>();
        var page = 1;
        const int pageSize = 2;
        int totalItemsReported = -1;

        while (true)
        {
            var body = await ListAsync(token, ct, page, pageSize);
            var items = Prop(body, "items", "Items");
            totalItemsReported = Prop(body, "totalItems", "TotalItems").GetInt32();

            if (items.GetArrayLength() == 0) break;
            foreach (var item in items.EnumerateArray())
            {
                var title = Prop(Prop(item, "data", "Data"), "Title").GetString()!;
                seenTitles.Add(title).Should().BeTrue($"'{title}' must not appear on more than one page");
            }

            page++;
            if (page > 20) throw new Xunit.Sdk.XunitException("pagination did not terminate — likely an infinite loop from a stuck TotalItems/TotalPages calculation");
        }

        totalItemsReported.Should().Be(4, "the last page's TotalItems must match the true visible count");
        seenTitles.Should().BeEquivalentTo(new[] { "Doc 0", "Doc 3", "Doc 6", "Doc 9" },
            "paginating through every page must surface exactly the 4 visible documents, no more, no fewer");
    }
}
