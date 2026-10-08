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
