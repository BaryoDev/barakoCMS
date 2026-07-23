using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Cover for the idempotency filter (H.2). The behaviour that regressed: a request that failed still
/// burned its idempotency key, so a legitimate retry was answered 409 forever. The finalizer now
/// releases the key on failure and only keeps it on success.
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

    private HttpRequestMessage Post(string token, string key, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/contents")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Idempotency-Key", key);
        return req;
    }

    private static object Valid(string title) => new
    {
        ContentType = $"idem_{Guid.NewGuid():N}",
        Data = new Dictionary<string, object> { ["Title"] = title },
        Status = 1,
    };

    private static object InvalidMissingType => new
    {
        ContentType = "", // fails the NotEmpty validator → 400, after the key is claimed
        Data = new Dictionary<string, object> { ["Title"] = "x" },
        Status = 1,
    };

    /// <summary>The regression: a failed request must not block a retry with the same key.</summary>
    [Fact]
    public async Task retry_after_a_failed_request_is_not_blocked_by_the_key()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var key = $"k-{Guid.NewGuid():N}";

        // First attempt fails validation (empty ContentType) — the key is claimed then must be released.
        var first = await _client.SendAsync(Post(token, key, InvalidMissingType));
        first.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The retry reuses the same key with a valid payload. It must go through, not 409.
        var retry = await _client.SendAsync(Post(token, key, Valid("recovered")));
        retry.StatusCode.Should().NotBe(HttpStatusCode.Conflict,
            "a request that failed never really happened, so its key must not block the retry");
        retry.IsSuccessStatusCode.Should().BeTrue();
    }

    /// <summary>The property that must be preserved: a genuine duplicate is still rejected.</summary>
    [Fact]
    public async Task a_successful_request_replayed_with_the_same_key_is_rejected()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var key = $"k-{Guid.NewGuid():N}";
        var body = Valid("once");

        var first = await _client.SendAsync(Post(token, key, body));
        first.IsSuccessStatusCode.Should().BeTrue();

        var dup = await _client.SendAsync(Post(token, key, body));
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict, "the same key after a success is a duplicate");
    }

    /// <summary>Keys are scoped per user, so two users can independently use the same raw key.</summary>
    [Fact]
    public async Task the_same_key_from_two_users_does_not_collide()
    {
        var (tokenA, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var (tokenB, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var sharedKey = $"shared-{Guid.NewGuid():N}";

        var a = await _client.SendAsync(Post(tokenA, sharedKey, Valid("from A")));
        a.IsSuccessStatusCode.Should().BeTrue();

        // User B uses the identical raw key — must not be treated as B duplicating A.
        var b = await _client.SendAsync(Post(tokenB, sharedKey, Valid("from B")));
        b.StatusCode.Should().NotBe(HttpStatusCode.Conflict, "the key is namespaced per user");
        b.IsSuccessStatusCode.Should().BeTrue();
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
        (await _client.SendAsync(r2)).IsSuccessStatusCode.Should().BeTrue("no key means no idempotency");
    }
}
