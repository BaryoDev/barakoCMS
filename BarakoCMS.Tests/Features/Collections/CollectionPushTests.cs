using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Collections;

/// <summary>
/// Entries pushed into a collection by the source that owns them (#991).
/// </summary>
/// <remarks>
/// The caller in mind is a repository's CI posting its changelog or docs when they change, so most
/// of these go through an API key rather than a session, the way that caller would.
/// </remarks>
[Collection("Sequential")]
public class CollectionPushTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private readonly IntegrationTestFixture _factory;

    public CollectionPushTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task A_push_creates_one_published_entry_per_slug()
    {
        var type = await ArrangeTypeAsync();
        var client = await AdminAsync();

        var (status, body) = await PushAsync(client, type, new { entries = new[] { Entry("4-3-0", "4.3.0"), Entry("4-2-0", "4.2.0") } });

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        Count(body, "created").Should().Be(2);
        Count(body, "updated").Should().Be(0);
        Count(body, "unchanged").Should().Be(0);
        Count(body, "archived").Should().Be(0);

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(2);
        entries.Select(e => Value(e, "slug")).Should().BeEquivalentTo(["4-3-0", "4-2-0"]);
        entries.Should().OnlyContain(e => e.Status == ContentStatus.Published);
    }

    [Fact]
    public async Task A_second_push_updates_the_changed_entry_and_leaves_the_unchanged_one_alone()
    {
        var type = await ArrangeTypeAsync();
        var client = await AdminAsync();

        await PushOkAsync(client, type, new { entries = new[] { Entry("a", "First"), Entry("b", "Second") } });
        var before = await EntriesAsync(type);
        before.Should().HaveCount(2);
        var versions = await StreamVersionsAsync(before);

        var (status, body) = await PushAsync(client, type, new { entries = new[] { Entry("a", "First, edited"), Entry("b", "Second") } });

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        Count(body, "created").Should().Be(0);
        Count(body, "updated").Should().Be(1);
        Count(body, "unchanged").Should().Be(1);

        var after = await EntriesAsync(type);
        after.Should().HaveCount(2, "an upsert by slug must not duplicate");
        Value(after.Single(e => Value(e, "slug") == "a"), "title").Should().Be("First, edited");

        var afterVersions = await StreamVersionsAsync(after);
        var a = before.Single(e => Value(e, "slug") == "a").Id;
        var b = before.Single(e => Value(e, "slug") == "b").Id;
        afterVersions[a].Should().Be(versions[a] + 1, "the changed entry got one new version");
        afterVersions[b].Should().Be(versions[b], "an unchanged entry gets no new version");
    }

    [Fact]
    public async Task An_unchanged_push_fires_no_webhook_while_a_changed_one_does()
    {
        var type = await ArrangeTypeAsync();
        var workflowId = await ArrangeWebhookOnUpdateAsync(type);
        var client = await AdminAsync();

        await PushOkAsync(client, type, new { entries = new[] { Entry("a", "First") } });
        var id = (await EntriesAsync(type)).Single().Id;

        await PushOkAsync(client, type, new { entries = new[] { Entry("a", "Changed") } });
        await WaitForDeliveriesAsync(workflowId, atLeast: 1);
        var version = (await StreamVersionsAsync(await EntriesAsync(type)))[id];

        var (status, body) = await PushAsync(client, type, new { entries = new[] { Entry("a", "Changed") } });
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        Count(body, "unchanged").Should().Be(1);

        // Room for the daemon to produce a delivery if the unchanged push appended anything.
        await Task.Delay(TimeSpan.FromSeconds(3));

        (await DeliveriesAsync(workflowId)).Should().Be(1,
            "the changed push delivered once and the unchanged push delivered nothing");
        (await StreamVersionsAsync(await EntriesAsync(type)))[id].Should().Be(version,
            "an unchanged push appends no event");
    }

    [Fact]
    public async Task ArchiveMissing_archives_published_entries_the_push_left_out_and_nothing_else()
    {
        var type = await ArrangeTypeAsync();
        var client = await AdminAsync();

        await PushOkAsync(client, type, new { entries = new[] { Entry("a", "A"), Entry("b", "B"), Entry("gone", "Gone") } });
        await PushOkAsync(client, type, new { status = "Draft", entries = new[] { Entry("draft", "Draft") } });

        var (status, body) = await PushAsync(client, type, new
        {
            archiveMissing = true,
            entries = new[] { Entry("a", "A"), Entry("b", "B") },
        });

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        Count(body, "archived").Should().Be(1);
        Count(body, "unchanged").Should().Be(2);

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(4);
        Status(entries, "gone").Should().Be(ContentStatus.Archived);
        Status(entries, "a").Should().Be(ContentStatus.Published);
        Status(entries, "b").Should().Be(ContentStatus.Published);
        Status(entries, "draft").Should().Be(ContentStatus.Draft, "only published entries are archived");
    }

    [Fact]
    public async Task One_invalid_entry_rolls_back_the_whole_push_and_archives_nothing()
    {
        var type = await ArrangeTypeAsync();
        var client = await AdminAsync();

        await PushOkAsync(client, type, new { entries = new[] { Entry("a", "A"), Entry("b", "B") } });

        var (status, body) = await PushAsync(client, type, new
        {
            archiveMissing = true,
            entries = new object[]
            {
                Entry("a", "A, edited"),
                Entry("new", "New"),
                new Dictionary<string, object> { ["slug"] = "broken", ["count"] = "not a number" },
            },
        });

        status.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().ContainSingle();
        errors[0].GetProperty("index").GetInt32().Should().Be(2);
        errors[0].GetProperty("slug").GetString().Should().Be("broken");
        errors[0].GetProperty("messages").EnumerateArray().Should().NotBeEmpty();

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(2, "the new entry was not written");
        Value(entries.Single(e => Value(e, "slug") == "a"), "title").Should().Be("A", "the valid update was rolled back");
        Status(entries, "b").Should().Be(ContentStatus.Published, "archiving waits for every write to succeed");
    }

    [Fact]
    public async Task The_same_slug_twice_in_one_push_is_refused()
    {
        var type = await ArrangeTypeAsync();

        var (status, body) = await PushAsync(await AdminAsync(), type,
            new { entries = new[] { Entry("same", "One"), Entry("same", "Two") } });

        status.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().ContainSingle("the first holder of the slug is fine, the second is the mistake");
        errors[0].GetProperty("index").GetInt32().Should().Be(1);
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_push_over_the_entry_limit_is_refused()
    {
        var type = await ArrangeTypeAsync();
        var entries = Enumerable.Range(0, barakoCMS.Features.Collections.Push.Limits.MaxEntries + 1)
            .Select(i => Entry($"e{i}", "x"))
            .ToArray();

        var (status, body) = await PushAsync(await AdminAsync(), type, new { entries });

        status.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    /// <summary>
    /// On a real Kestrel host, because the in-memory test server does not enforce a body limit.
    /// </summary>
    [Fact]
    public async Task A_body_over_the_push_size_limit_is_413()
    {
        var type = await ArrangeTypeAsync();

        using var host = _factory.WithWebHostBuilder(_ => { });
        host.UseKestrel(0);
        host.StartServer();
        using var client = host.CreateClient();

        var oversized = new string('a', (int)barakoCMS.Features.Collections.Push.Limits.MaxBodyBytes + 1024);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/collections/{type}/push")
        {
            Content = new StringContent(
                $$$"""{"entries":[{"slug":"big","title":"{{{oversized}}}"}]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));
        request.Headers.ExpectContinue = true;

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_anonymous_push_is_refused()
    {
        var type = await ArrangeTypeAsync();

        var (status, _) = await PushAsync(_factory.CreateClient(), type, new { entries = new[] { Entry("a", "A") } });

        status.Should().Be(HttpStatusCode.Unauthorized);
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_caller_without_permission_on_the_type_is_refused()
    {
        var type = await ArrangeTypeAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("PushNobody"));

        var (status, _) = await PushAsync(client, type, new { entries = new[] { Entry("a", "A") } });

        status.Should().Be(HttpStatusCode.Forbidden);
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_key_limited_to_the_type_can_push_to_it()
    {
        var type = await ArrangeTypeAsync();
        var secret = await StoreKeyAsync(["content:write"], contentTypes: [type]);

        var (status, body) = await PushAsync(KeyClient(secret), type, new { entries = new[] { Entry("a", "A") } });

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        (await EntriesAsync(type)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_key_limited_to_another_type_is_refused()
    {
        var type = await ArrangeTypeAsync();
        var other = await ArrangeTypeAsync();
        var secret = await StoreKeyAsync(["content:write"], contentTypes: [other]);

        var (status, _) = await PushAsync(KeyClient(secret), type, new { entries = new[] { Entry("a", "A") } });

        status.Should().Be(HttpStatusCode.Forbidden);
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_key_limited_to_named_types_cannot_reach_the_rest_of_the_content_api()
    {
        var type = await ArrangeTypeAsync();
        var secret = await StoreKeyAsync(["content:read", "content:write"], contentTypes: [type]);
        var client = KeyClient(secret);

        var list = await client.GetAsync("/api/contents", TestContext.Current.CancellationToken);
        var create = await client.PostAsJsonAsync("/api/contents",
            new { contentType = type, status = "Published", data = Entry("side-door", "Side door") },
            TestContext.Current.CancellationToken);

        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_key_without_content_write_is_refused()
    {
        var type = await ArrangeTypeAsync();
        var secret = await StoreKeyAsync(["content:read"], contentTypes: [type]);

        var (status, _) = await PushAsync(KeyClient(secret), type, new { entries = new[] { Entry("a", "A") } });

        status.Should().Be(HttpStatusCode.Forbidden);
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    /// <summary>
    /// A key acts in its own tenant whatever the request says, so it cannot reach this one.
    /// </summary>
    /// <remarks>
    /// The type exists in both tenants, so a refusal is not just the type being missing: the key's
    /// push lands in its own tenant and this tenant's collection stays empty.
    /// </remarks>
    [Fact]
    public async Task A_key_for_another_tenant_cannot_write_into_this_one()
    {
        var type = await ArrangeTypeAsync();
        var elsewhere = "push-" + Guid.NewGuid().ToString("n")[..8];
        await ArrangeTypeAsync(type, elsewhere);

        var secret = await StoreKeyAsync(["content:write"], contentTypes: [type], tenant: elsewhere);
        var client = KeyClient(secret);
        client.DefaultRequestHeaders.Add("X-Tenant", Tenant.DefaultSlug);

        var (status, body) = await PushAsync(client, type, new { entries = new[] { Entry("a", "A") } });

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        (await EntriesAsync(type)).Should().BeEmpty("the key belongs to another tenant");
        (await EntriesAsync(type, elsewhere)).Should().ContainSingle("the push landed in the key's own tenant");
    }

    [Fact]
    public async Task A_key_for_another_tenant_is_refused_a_type_only_this_tenant_has()
    {
        var type = await ArrangeTypeAsync();
        var secret = await StoreKeyAsync(["content:write"], contentTypes: [type], tenant: "push-" + Guid.NewGuid().ToString("n")[..8]);
        var client = KeyClient(secret);
        client.DefaultRequestHeaders.Add("X-Tenant", Tenant.DefaultSlug);

        var (status, _) = await PushAsync(client, type, new { entries = new[] { Entry("a", "A") } });

        status.Should().Be(HttpStatusCode.NotFound, "in the key's tenant there is no such type");
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_repeated_idempotency_key_is_answered_409()
    {
        var type = await ArrangeTypeAsync();
        var client = await AdminAsync();
        var key = Guid.NewGuid().ToString("n");

        async Task<HttpStatusCode> SendAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/collections/{type}/push")
            {
                Content = JsonContent.Create(new { entries = new[] { Entry("a", "A") } }),
            };
            request.Headers.Add("Idempotency-Key", key);
            return (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
        }

        (await SendAsync()).Should().Be(HttpStatusCode.OK);
        (await SendAsync()).Should().Be(HttpStatusCode.Conflict);
        (await EntriesAsync(type)).Should().ContainSingle();
    }

    [Fact]
    public async Task An_admin_mints_a_key_limited_to_named_types_and_the_listing_shows_them()
    {
        var type = await ArrangeTypeAsync();
        var client = await AdminAsync();

        var created = await client.PostAsJsonAsync("/api/api-keys",
            new { name = "changelog CI", scopes = new[] { "content:write" }, contentTypes = new[] { type } },
            TestContext.Current.CancellationToken);
        var createdBody = await Json(created);

        created.StatusCode.Should().Be(HttpStatusCode.OK, createdBody.ToString());
        createdBody.GetProperty("contentTypes").EnumerateArray().Select(t => t.GetString()).Should().Equal(type);

        var (status, body) = await PushAsync(KeyClient(createdBody.GetProperty("key").GetString()!), type,
            new { entries = new[] { Entry("a", "A") } });
        status.Should().Be(HttpStatusCode.OK, body.ToString());
    }

    [Fact]
    public async Task Minting_a_key_for_a_type_that_does_not_exist_is_refused()
    {
        var created = await (await AdminAsync()).PostAsJsonAsync("/api/api-keys",
            new { name = "typo", scopes = new[] { "content:write" }, contentTypes = new[] { "no-such-type-" + Guid.NewGuid().ToString("n")[..6] } },
            TestContext.Current.CancellationToken);

        created.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static Dictionary<string, object> Entry(string slug, string title) => new()
    {
        ["slug"] = slug,
        ["title"] = title,
    };

    private async Task<string> ArrangeTypeAsync(string? name = null, string tenant = Tenant.DefaultSlug)
    {
        var type = name ?? "push" + Guid.NewGuid().ToString("n")[..10];

        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var session = tenant == Tenant.DefaultSlug ? store.LightweightSession() : store.LightweightSession(tenant);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Release",
            Fields =
            [
                new FieldDefinition { Name = "slug", Type = "slug", IsRequired = true },
                new FieldDefinition { Name = "title", Type = "string" },
                new FieldDefinition { Name = "count", Type = "int" },
            ],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return type;
    }

    private async Task<Guid> ArrangeWebhookOnUpdateAsync(string type)
    {
        // Loopback, which the outbound guard refuses before any socket opens. The refusal still
        // leaves a delivery row, and the row is what is counted.
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = $"push-probe-{Guid.NewGuid():N}",
            TriggerContentType = type,
            TriggerEvent = "Updated",
            Actions =
            [
                new WorkflowAction
                {
                    Type = "Webhook",
                    Parameters = new Dictionary<string, string> { ["Url"] = "http://127.0.0.1:9/push-probe" },
                },
            ],
        };

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(workflow);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return workflow.Id;
    }

    private async Task<int> DeliveriesAsync(Guid workflowId)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<WebhookDelivery>().CountAsync(d => d.WorkflowId == workflowId);
    }

    private async Task WaitForDeliveriesAsync(Guid workflowId, int atLeast)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await DeliveriesAsync(workflowId) >= atLeast) return;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new Xunit.Sdk.XunitException(
            $"Timed out after {PollTimeout.TotalSeconds:0}s waiting for {atLeast} delivery row(s). A webhook "
          + "that never fires would pass the no-delivery assertion on its own.");
    }

    private async Task<Dictionary<Guid, long>> StreamVersionsAsync(IEnumerable<Content> entries)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

        var versions = new Dictionary<Guid, long>();
        foreach (var entry in entries)
        {
            var state = await session.Events.FetchStreamStateAsync(entry.Id, TestContext.Current.CancellationToken);
            versions[entry.Id] = state!.Version;
        }

        return versions;
    }

    private async Task<string> StoreKeyAsync(string[] scopes, string[] contentTypes, string tenant = Tenant.DefaultSlug)
    {
        Guid ownerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var role = await session.Query<Role>().FirstAsync(r => r.Name == "SuperAdmin", TestContext.Current.CancellationToken);
            ownerId = Guid.NewGuid();
            session.Store(new User
            {
                Id = ownerId,
                Username = $"push-{ownerId:n}",
                Email = $"push-{ownerId:n}@example.com",
                RoleIds = [role.Id],
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ApiKey
            {
                Id = Guid.NewGuid(),
                Name = "push",
                KeyHash = ApiKeyService.Hash(secret),
                Prefix = secret[..12],
                UserId = ownerId,
                TenantSlug = tenant,
                Scopes = scopes.ToList(),
                ContentTypes = contentTypes.ToList(),
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return secret;
    }

    private HttpClient KeyClient(string secret)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return client;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PushAsync(HttpClient client, string type, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/collections/{type}/push", body, TestContext.Current.CancellationToken);
        return (response.StatusCode, await Json(response));
    }

    private static async Task PushOkAsync(HttpClient client, string type, object body)
    {
        var (status, json) = await PushAsync(client, type, body);
        status.Should().Be(HttpStatusCode.OK, json.ToString());
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (string.IsNullOrWhiteSpace(text)) return default;

        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse(JsonSerializer.Serialize(text)).RootElement.Clone();
        }
    }

    private static int Count(JsonElement body, string name) => body.GetProperty(name).GetInt32();

    private async Task<List<Content>> EntriesAsync(string type, string tenant = Tenant.DefaultSlug)
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var session = tenant == Tenant.DefaultSlug ? store.QuerySession() : store.QuerySession(tenant);

        return (await session.Query<Content>()
            .Where(c => c.ContentType == type)
            .ToListAsync(TestContext.Current.CancellationToken)).ToList();
    }

    private static string? Value(Content entry, string field) =>
        entry.Data.TryGetValue(field, out var value) ? value?.ToString() : null;

    private static ContentStatus Status(List<Content> entries, string slug) =>
        entries.Single(e => Value(e, "slug") == slug).Status;
}
