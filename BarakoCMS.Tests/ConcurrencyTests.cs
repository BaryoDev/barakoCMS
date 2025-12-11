using System.Net;
using System.Net.Http.Json;
using Xunit;
using FluentAssertions;
using FastEndpoints.Security;

namespace BarakoCMS.Tests;

public class ConcurrencyTests : IClassFixture<IntegrationTestFixture>
{
    private readonly HttpClient _client;

    public ConcurrencyTests(IntegrationTestFixture factory)
    {
        _client = factory.CreateClient();
    }

    private string CreateToken()
    {
        return JWTBearer.CreateToken(
            signingKey: "test-super-secret-key-that-is-at-least-32-chars-long",
            expireAt: DateTime.UtcNow.AddDays(1),
            privileges: u =>
            {
                u.Roles.Add("Admin");
                u.Claims.Add(new("UserId", Guid.NewGuid().ToString()));
            });
    }

    [Fact]
    public async Task UpdateContent_ShouldFail_WhenVersionInternalMismatch()
    {
        // 1. Arrange: Create Content Type
        var token = CreateToken();
        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var ctypeReq = new { name = "Article", fields = new Dictionary<string, string> { { "Title", "string" } } };
        await _client.PostAsJsonAsync("/api/content-types", ctypeReq);

        // 2. Create Content
        var contentId = Guid.NewGuid();
        var createReq = new
        {
            Id = contentId,
            ContentType = "Article",
            Data = new Dictionary<string, object> { { "Title", "Original Title" } }
        };
        var createResp = await _client.PostAsJsonAsync("/api/contents", createReq);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 3. Update Content (v1 -> v2)
        // We assume created content is at v1.
        // We send v1 expectedVersion (so next is v2).
        var updateReq1 = new
        {
            Data = new Dictionary<string, object> { { "Title", "Updated Title 1" } },
            Version = 1 // Expected version (Marten Append expects expectedVersion + 1 ?? No wait)
                        // Endpoint logic: _session.Events.Append(req.Id, req.Version + 1, @event);
                        // If we are at v1, and want to create v2.
                        // Marten Append(id, expectedVersion, event) checks if stream is AT expectedVersion??
                        // Wait, Marten API: Append(id, version, events) asserts that the NEW version will represent 'version'.
                        // OR Append(id, expectedVersion, events) asserts stream is AT expectedVersion?

            // Re-read Endpoint.cs:
            // _session.Events.Append(req.Id, req.Version + 1, @event);
            // If req.Version is the CURRENT (client known) version, e.g. 1.
            // We are passing 2.
            // Marten's Append(id, int version, events) -> "Optimistic Concurrency: Append events calculating the version number from the supplied version."
            // Wait, Marten Append overload with long version usually means "This is the EXPECTED version of the stream".
            // Or "This is the TARGET version"?

            // Marten documentation says:
            // Append(streamId, expectedVersion, events)
            // "Marten will check that the max version of the stream is equal to the expectedVersion"

            // Endpoint code: `_session.Events.Append(req.Id, req.Version + 1, @event);`
            // If Client says "I am at v1" (req.Version = 1).
            // We pass 2.
            // Does this mean we expect stream to be at 2? NO.

            // If the code is: `Append(id, expectedVersion, events)`
            // Then passing `req.Version + 1` (2) means we expect stream to be at 2 BEFORE appending? No that fails.

            // If the code is `Append(id, events)` -> Just appends.

            // Optimistic concurrency in Marten usually uses `StartStream(..., events)` or `Append(..., events)` but explicit checks need `.Append(..., expectedVersion: 1, ...)`?

            // Let's assume the Endpoint logic is flawed or using a specific overload.
            // Overload: Append(Guid stream, long expectedVersion, IEnumerable<object> events)

            // If Endpoint uses `req.Version + 1`, maybe it INTENDS to say "The new version should be X"?
            // Marten 4+: `session.Events.Append(id, expectedVersion: 1, events)` means verify stream is at v1.
            // Endpoint has: `_session.Events.Append(req.Id, req.Version + 1, @event);`

            // I need to correct the Endpoint logic if it's wrong, OR verify what it does.
            // If I am at v1. I pass v1. Endpoint calls Append(id, 2, event). 
            // If this overload means "Make sure the result is v2", then it's fine.
            // If this overload means "Assert stream is at v2 key", then it will fail immediately.

            // I suspect `req.Version` should be passed directly if the parameter name is `expectedVersion`.
            // But if the params are (id, version, events), maybe `version` means "Expected version"?

            // Let's try to assume my test goal is to create a conflict.
            // I'll update once (success). 
            // Then update AGAIN with SAME version (fail).
        };

        // First update: Client thinks v1.
        var req1 = new { Data = new Dictionary<string, object> { { "Title", "Update 1" } }, Version = 1 };
        var resp1 = await _client.PutAsJsonAsync($"/api/contents/{contentId}", req1);
        resp1.EnsureSuccessStatusCode();
        // This succeeded, so now stream should be at v2.

        // Second update: Client STILL thinks v1 (Conclict).
        var req2 = new { Data = new Dictionary<string, object> { { "Title", "Update 2" } }, Version = 1 };
        var resp2 = await _client.PutAsJsonAsync($"/api/contents/{contentId}", req2);

        // Assert
        resp2.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed); // 412
    }
}
