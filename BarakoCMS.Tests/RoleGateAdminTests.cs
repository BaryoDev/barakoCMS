using System.Net;
using FluentAssertions;
using Xunit;
using static BarakoCMS.Tests.RoleGateTests;

namespace BarakoCMS.Tests;

/// <summary>The served half of <see cref="RoleGateTests"/>, in its own class so it runs beside the other two.</summary>
[Collection("Integration4")]
public class RoleGateAdminTests(IntegrationTestFixture factory)
{
    // One caller for the whole class, rather than one per theory case. The database is shared with
    // every other test in the collection, and a hundred throwaway users would sit at the top of
    // GET /api/users, where other tests read the first row.
    private static readonly Lock Gate = new();
    private static Task<HttpClient>? _admin;

    private Task<HttpClient> Client()
    {
        lock (Gate) return _admin ??= AuthedAsStoredUser(factory, "SuperAdmin", "Admin");
    }

    /// <summary>
    /// The positive control. Without it a gate that refuses everyone, or a route that no longer
    /// exists, would leave both refusal theories green.
    /// </summary>
    /// <remarks>
    /// The admin is refused nothing and reaches the endpoint: 401 and 403 mean the gate turned an
    /// allowed role away, and 404 means routing found nothing to run. What comes back instead is
    /// usually a 400 from binding the deliberately unparseable id or body, which only the endpoint
    /// itself can produce, and which mutates nothing.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllGatedRoutes), MemberType = typeof(RoleGateTests))]
    public async Task Still_answers_an_admin(GatedRoute route)
    {
        var client = await Client();

        var response = await client.SendAsync(Request(route));

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "an admin holding the gated role must not be turned away by authentication");
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "a gate that refuses an admin too would make the two refusal tests pass for the wrong reason");
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            "routing must still reach an endpoint here, otherwise the refusals above only prove the route is gone");
    }
}
