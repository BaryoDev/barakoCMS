using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Filters;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Cover for the idempotency filter. A failed request releases its key; a successful one keeps it
/// and its response, so a retry of the same request from the same caller gets that response back
/// (issue #612).
/// </summary>
[Collection("Sequential")]
public class IdempotencyTests
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _factory;

    public IdempotencyTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static HttpRequestMessage Post(string token, string key, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/contents")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Idempotency-Key", key);
        return req;
    }

    private static object Body(string contentType, string title) => new
    {
        ContentType = contentType,
        Data = new Dictionary<string, object> { ["Title"] = title },
        Status = 1,
    };

    private static object Valid(string title) => Body($"idem_{Guid.NewGuid():N}", title);

    private static object InvalidMissingType => new
    {
        ContentType = "", // fails the NotEmpty validator, giving a 400 after the key is claimed
        Data = new Dictionary<string, object> { ["Title"] = "x" },
        Status = 1,
    };

    private async Task<List<Content>> EntriesAsync(string contentType)
    {
        using var scope = _factory.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await query.Query<Content>()
            .Where(c => c.ContentType == contentType)
            .ToListAsync(TestContext.Current.CancellationToken)).ToList();
    }

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static bool WasReplayed(HttpResponseMessage response) =>
        response.Headers.TryGetValues(IdempotencyFilter.ReplayedHeader, out var values)
        && values.SequenceEqual(["true"]);

    /// <summary>A failed request must not block a retry with the same key.</summary>
    [Fact]
    public async Task retry_after_a_failed_request_is_not_blocked_by_the_key()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var key = $"k-{Guid.NewGuid():N}";

        var first = await _client.SendAsync(Post(token, key, InvalidMissingType));
        first.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The retry reuses the key with a different, valid body. The failed attempt left nothing
        // behind, so this is a fresh request rather than a mismatch.
        var retry = await _client.SendAsync(Post(token, key, Valid("recovered")));
        retry.IsSuccessStatusCode.Should().BeTrue(
            "a request that failed never really happened, so its key must not block the retry");
        WasReplayed(retry).Should().BeFalse();
    }

    /// <summary>
    /// The case idempotency exists for: the client never saw the first answer, so the retry has to
    /// carry it, including the id that was created, and must not create a second entry.
    /// </summary>
    [Fact]
    public async Task a_content_post_replayed_with_the_same_key_returns_the_first_response_and_creates_one_entry()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var key = $"k-{Guid.NewGuid():N}";
        var contentType = $"idem_{Guid.NewGuid():N}";
        var body = Body(contentType, "posted once");

        var first = await _client.SendAsync(Post(token, key, body));
        first.IsSuccessStatusCode.Should().BeTrue();
        WasReplayed(first).Should().BeFalse();
        var firstBody = await first.Content.ReadAsStringAsync();

        var replay = await _client.SendAsync(Post(token, key, body));
        replay.StatusCode.Should().Be(first.StatusCode);
        WasReplayed(replay).Should().BeTrue();
        replay.Content.Headers.ContentType?.MediaType.Should().Be(first.Content.Headers.ContentType?.MediaType);
        (await replay.Content.ReadAsStringAsync()).Should().Be(firstBody);

        var entries = await EntriesAsync(contentType);
        entries.Should().HaveCount(1, "the replay must not have created a second entry");
        entries[0].Id.Should().Be(await IdOfAsync(replay), "the replay carries the id the first call created");
    }

    /// <summary>Keys are scoped per caller, so another caller's identical key is its own key.</summary>
    [Fact]
    public async Task the_same_key_from_a_different_caller_creates_its_own_entry()
    {
        var (tokenA, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var (tokenB, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var sharedKey = $"shared-{Guid.NewGuid():N}";
        var contentType = $"idem_{Guid.NewGuid():N}";
        var body = Body(contentType, "same body");

        var a = await _client.SendAsync(Post(tokenA, sharedKey, body));
        a.IsSuccessStatusCode.Should().BeTrue();

        var b = await _client.SendAsync(Post(tokenB, sharedKey, body));
        b.IsSuccessStatusCode.Should().BeTrue();
        WasReplayed(b).Should().BeFalse("a response is never replayed to a caller other than the one who made it");

        var entries = await EntriesAsync(contentType);
        entries.Should().HaveCount(2);
        (await IdOfAsync(b)).Should().NotBe(await IdOfAsync(a));
    }

    [Fact]
    public async Task the_same_key_with_a_different_body_is_answered_422()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var key = $"k-{Guid.NewGuid():N}";
        var contentType = $"idem_{Guid.NewGuid():N}";

        var first = await _client.SendAsync(Post(token, key, Body(contentType, "first")));
        first.IsSuccessStatusCode.Should().BeTrue();

        var other = await _client.SendAsync(Post(token, key, Body(contentType, "something else")));
        other.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        WasReplayed(other).Should().BeFalse();
        (await other.Content.ReadAsStringAsync()).Should().Be(IdempotencyFilter.DifferentRequestMessage);

        (await EntriesAsync(contentType)).Should().HaveCount(1);
    }

    [Fact]
    public async Task a_response_over_the_stored_limit_is_not_replayed()
    {
        // Eight bytes is smaller than any create response, so this one is not kept.
        WebApplicationFactory<Program> host = _factory.WithSetting(IdempotencyOptions.MaxStoredResponseBytesKey, "8");
        var client = host.CreateClient();
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var key = $"k-{Guid.NewGuid():N}";
        var contentType = $"idem_{Guid.NewGuid():N}";
        var body = Body(contentType, "too big to keep");

        var first = await client.SendAsync(Post(token, key, body));
        first.IsSuccessStatusCode.Should().BeTrue();

        var replay = await client.SendAsync(Post(token, key, body));
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        WasReplayed(replay).Should().BeFalse();
        (await replay.Content.ReadAsStringAsync()).Should().Be(IdempotencyFilter.TooLargeMessage);

        (await EntriesAsync(contentType)).Should().HaveCount(1);
    }

    /// <summary>
    /// Time is moved by ageing the stored record, the same thing the clock passing would do. No sweep
    /// runs here: the filter itself treats an expired key as free.
    /// </summary>
    [Fact]
    public async Task an_expired_key_is_accepted_as_a_new_request()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var key = $"k-{Guid.NewGuid():N}";
        var contentType = $"idem_{Guid.NewGuid():N}";
        var body = Body(contentType, "posted twice, a day apart");

        var first = await _client.SendAsync(Post(token, key, body));
        first.IsSuccessStatusCode.Should().BeTrue();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var records = await session.Query<IdempotencyRecord>()
                .Where(r => r.Key.EndsWith(key))
                .ToListAsync(TestContext.Current.CancellationToken);
            records.Should().HaveCount(1);
            records[0].CreatedAt = DateTime.UtcNow.AddHours(-(IdempotencyOptions.DefaultKeyHours + 1));
            session.Store(records[0]);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var again = await _client.SendAsync(Post(token, key, body));
        again.IsSuccessStatusCode.Should().BeTrue();
        WasReplayed(again).Should().BeFalse("the key expired, so this is a new request");

        var entries = await EntriesAsync(contentType);
        entries.Should().HaveCount(2);
        (await IdOfAsync(again)).Should().NotBe(await IdOfAsync(first));
    }

    private async Task<List<IdempotencyRecord>> RecordsAsync(string rawKey)
    {
        using var scope = _factory.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await query.Query<IdempotencyRecord>()
            .Where(r => r.Key.EndsWith(rawKey))
            .ToListAsync(TestContext.Current.CancellationToken)).ToList();
    }

    [Fact]
    public async Task an_api_key_and_the_user_it_acts_for_are_separate_callers()
    {
        var (token, userId) = await TestHelpers.CreateAdminUserAsync(_factory);
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ApiKey
            {
                Id = Guid.NewGuid(),
                Name = "idempotency",
                KeyHash = barakoCMS.Infrastructure.Auth.ApiKeyService.Hash(secret),
                Prefix = secret[..12],
                UserId = userId,
                TenantSlug = Tenant.DefaultSlug,
                Scopes = [ApiKeyScopes.ContentWrite],
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var key = $"k-{Guid.NewGuid():N}";
        var contentType = $"idem_{Guid.NewGuid():N}";
        var body = Body(contentType, "same body, two callers");

        var asUser = await _client.SendAsync(Post(token, key, body));
        asUser.IsSuccessStatusCode.Should().BeTrue();

        var asKey = await _client.SendAsync(Post(secret, key, body));
        asKey.IsSuccessStatusCode.Should().BeTrue(await asKey.Content.ReadAsStringAsync());
        WasReplayed(asKey).Should().BeFalse("the key is its own caller, not the user's session");

        var entries = await EntriesAsync(contentType);
        entries.Should().HaveCount(2);
        (await IdOfAsync(asKey)).Should().NotBe(await IdOfAsync(asUser));
    }

    [Fact]
    public async Task the_same_key_on_a_different_method_and_path_is_answered_422()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var key = $"k-{Guid.NewGuid():N}";
        var contentType = $"idem_{Guid.NewGuid():N}";
        var body = Body(contentType, "created");

        var created = await _client.SendAsync(Post(token, key, body));
        created.IsSuccessStatusCode.Should().BeTrue();
        var id = await IdOfAsync(created);

        // The same body, sent as an update of the entry the create made.
        var update = new HttpRequestMessage(HttpMethod.Put, $"/api/contents/{id}") { Content = JsonContent.Create(body) };
        update.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        update.Headers.Add("Idempotency-Key", key);

        var answer = await _client.SendAsync(update);
        answer.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await answer.Content.ReadAsStringAsync()).Should().Be(IdempotencyFilter.DifferentRequestMessage);
    }

    [Fact]
    public async Task the_same_key_on_a_different_path_with_the_same_method_is_answered_422()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var key = $"k-{Guid.NewGuid():N}";
        var body = Valid("created");

        (await _client.SendAsync(Post(token, key, body))).IsSuccessStatusCode.Should().BeTrue();

        var elsewhere = new HttpRequestMessage(HttpMethod.Post, "/api/api-keys") { Content = JsonContent.Create(body) };
        elsewhere.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        elsewhere.Headers.Add("Idempotency-Key", key);

        var answer = await _client.SendAsync(elsewhere);
        answer.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task the_same_key_with_a_different_query_string_is_answered_422()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var key = $"k-{Guid.NewGuid():N}";
        var contentType = $"idem_{Guid.NewGuid():N}";
        var body = Body(contentType, "same body");

        HttpRequestMessage WithQuery(string query)
        {
            var req = Post(token, key, body);
            req.RequestUri = new Uri("/api/contents" + query, UriKind.Relative);
            return req;
        }

        (await _client.SendAsync(WithQuery("?source=a"))).IsSuccessStatusCode.Should().BeTrue();

        var other = await _client.SendAsync(WithQuery("?source=b"));
        other.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await EntriesAsync(contentType)).Should().HaveCount(1);
    }

    /// <summary>
    /// Every unauthenticated caller shares one key bucket, so replaying there would hand one
    /// stranger's response to another. They get dedupe only.
    /// </summary>
    [Fact]
    public async Task an_anonymous_retry_is_answered_409_and_nothing_is_stored()
    {
        var key = $"k-{Guid.NewGuid():N}";

        HttpRequestMessage Logout()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
            req.Headers.Add("Cookie", "barako_refresh=" + Guid.NewGuid().ToString("N"));
            req.Headers.Add("Idempotency-Key", key);
            return req;
        }

        var first = await _client.SendAsync(Logout());
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        var retry = await _client.SendAsync(Logout());
        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        WasReplayed(retry).Should().BeFalse();
        (await retry.Content.ReadAsStringAsync()).Should().Be(IdempotencyFilter.AlreadyProcessedMessage);

        var records = await RecordsAsync(key);
        records.Should().HaveCount(1);
        records[0].Completed.Should().BeTrue();
        records[0].Replayable.Should().BeFalse();
        records[0].Method.Should().BeNull("nothing about an anonymous request is kept");
        records[0].ProtectedResponseBody.Should().BeNull();
        records[0].StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task a_keyed_api_key_create_retried_gets_409_and_stores_no_body()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var key = $"k-{Guid.NewGuid():N}";
        var name = $"idem-{Guid.NewGuid():N}";
        var body = new { name, scopes = new[] { ApiKeyScopes.ContentRead } };

        HttpRequestMessage Create()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/api-keys") { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Idempotency-Key", key);
            return req;
        }

        var first = await _client.SendAsync(Create());
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        var retry = await _client.SendAsync(Create());
        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        WasReplayed(retry).Should().BeFalse();
        (await retry.Content.ReadAsStringAsync()).Should().Be(IdempotencyFilter.AlreadyProcessedMessage);

        var records = await RecordsAsync(key);
        records.Should().HaveCount(1);
        records[0].Completed.Should().BeTrue();
        records[0].Replayable.Should().BeFalse();
        records[0].ProtectedResponseBody.Should().BeNull("the response holds the raw API key");

        using var scope = _factory.Services.CreateScope();
        var keys = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<ApiKey>()
            .Where(k => k.Name == name)
            .ToListAsync(TestContext.Current.CancellationToken);
        keys.Should().HaveCount(1, "the retry did not mint a second key");
    }

    [Fact]
    public async Task a_stored_response_is_encrypted()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var key = $"k-{Guid.NewGuid():N}";

        var first = await _client.SendAsync(Post(token, key, Valid("stored")));
        first.IsSuccessStatusCode.Should().BeTrue();
        var id = (await IdOfAsync(first)).ToString();

        var records = await RecordsAsync(key);
        records.Should().HaveCount(1);
        records[0].Replayable.Should().BeTrue();
        records[0].ProtectedResponseBody.Should().NotBeNullOrEmpty();
        records[0].ProtectedResponseBody.Should().NotContain(id);
        // "eyJp" is the base64 of the body's first three bytes, {"i, so a body kept only as base64
        // would contain it.
        records[0].ProtectedResponseBody.Should().NotContain("eyJp");
    }

    /// <summary>
    /// Two retries found the same expired record. The first replaced it with a fresh claim; the
    /// second's reclaim must then fail rather than delete that fresh claim and run the handler too.
    /// </summary>
    [Fact]
    public async Task a_reclaim_does_not_remove_a_fresh_claim_another_retry_made()
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var key = $"reclaim-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        var lifetime = TimeSpan.FromHours(IdempotencyOptions.DefaultKeyHours);

        await using (var session = store.LightweightSession())
        {
            session.Store(new IdempotencyRecord { Key = key, Completed = false, CreatedAt = now, Method = "WINNER" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = store.LightweightSession())
        {
            var claimed = await IdempotencyFilter.TryClaimAsync(
                session, new IdempotencyRecord { Key = key, CreatedAt = now, Method = "LOSER" },
                replacing: true, lifetime, TestContext.Current.CancellationToken);
            claimed.Should().BeFalse();
        }

        var records = await RecordsAsync(key);
        records.Should().HaveCount(1);
        records[0].Method.Should().Be("WINNER");
    }

    [Fact]
    public async Task a_reclaim_replaces_an_expired_record()
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var key = $"reclaim-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        var lifetime = TimeSpan.FromHours(IdempotencyOptions.DefaultKeyHours);

        await using (var session = store.LightweightSession())
        {
            session.Store(new IdempotencyRecord { Key = key, Completed = true, CreatedAt = now - lifetime - TimeSpan.FromMinutes(1), Method = "OLD" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = store.LightweightSession())
        {
            var claimed = await IdempotencyFilter.TryClaimAsync(
                session, new IdempotencyRecord { Key = key, CreatedAt = now, Method = "NEW" },
                replacing: true, lifetime, TestContext.Current.CancellationToken);
            claimed.Should().BeTrue();
        }

        var records = await RecordsAsync(key);
        records.Should().HaveCount(1);
        records[0].Method.Should().Be("NEW");
    }

    /// <summary>A request without the header is unaffected.</summary>
    [Fact]
    public async Task requests_without_the_header_are_not_deduplicated()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var body = Valid("no key");

        var r1 = new HttpRequestMessage(HttpMethod.Post, "/api/contents") { Content = JsonContent.Create(body) };
        r1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var r2 = new HttpRequestMessage(HttpMethod.Post, "/api/contents") { Content = JsonContent.Create(body) };
        r2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await _client.SendAsync(r1)).IsSuccessStatusCode.Should().BeTrue();
        var second = await _client.SendAsync(r2);
        second.IsSuccessStatusCode.Should().BeTrue("no key means no idempotency");
        WasReplayed(second).Should().BeFalse();
    }
}
