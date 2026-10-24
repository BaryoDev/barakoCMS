using System.Net;
using FluentAssertions;
using Xunit;
using static BarakoCMS.Tests.RoleGateTests;

namespace BarakoCMS.Tests;

/// <summary>The 403 half of <see cref="RoleGateTests"/>, in its own class so it runs beside the other two.</summary>
[Collection("Integration1")]
public class RoleGateWrongRoleTests(IntegrationTestFixture factory)
{
    // One caller for the whole class, rather than one per theory case. The database is shared with
    // every other test in the collection, and a hundred throwaway users would sit at the top of
    // GET /api/users, where other tests read the first row.
    private static readonly Lock Gate = new();
    private static Task<HttpClient>? _wrongRole;

    private Task<HttpClient> Client()
    {
        lock (Gate) return _wrongRole ??= AuthedAsStoredUser(factory, "User");
    }

    [Theory]
    [MemberData(nameof(AllGatedRoutes), MemberType = typeof(RoleGateTests))]
    public async Task Refuses_a_signed_in_caller_without_an_admin_role(GatedRoute route)
    {
        var client = await Client();

        var response = await client.SendAsync(Request(route));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
